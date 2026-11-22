use std::collections::HashSet;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Instant;

use anyhow::Result;
use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::State;
use axum::response::IntoResponse;
use axum::routing::{get, post};
use axum::{Json, Router};
use dashmap::DashMap;
use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use tokio::sync::{mpsc, watch};
use tower_http::cors::CorsLayer;
use tracing::{info, warn};
use worldmill_core::{CameraPose, ChunkCoord, ChunkMesh, compute_desired_chunks};
use worldmill_gpu::GpuWorkerPool;

struct AppState {
    gpu: Arc<GpuWorkerPool>,
    /// In-memory encoded binary chunk cache (ready for zero-copy WebSocket send).
    mem_cache: DashMap<(ChunkCoord, u32), Arc<Vec<u8>>>,
    /// NVMe Zstd-compressed disk cache directory.
    cache_dir: PathBuf,
    /// Telemetry counters.
    gpu_bakes: AtomicU64,
    cache_hits: AtomicU64,
    total_bake_us: AtomicU64,
    total_triangles: AtomicU64,
}

impl AppState {
    async fn get_or_bake_chunk(&self, coord: ChunkCoord, seed: u32) -> Result<Arc<Vec<u8>>> {
        let key = (coord, seed);
        if let Some(hit) = self.mem_cache.get(&key) {
            self.cache_hits.fetch_add(1, Ordering::Relaxed);
            return Ok(Arc::clone(hit.value()));
        }

        // Check NVMe Zstd disk cache
        let disk_path = self.cache_dir.join(coord.cache_key(seed));
        if let Ok(compressed) = tokio::fs::read(&disk_path).await {
            if let Ok(mut raw) = zstd::decode_all(&compressed[..]) {
                // Mark cache_hit flag in header (byte 36..40)
                if raw.len() >= 40 {
                    raw[36..40].copy_from_slice(&1u32.to_le_bytes());
                }
                let arc = Arc::new(raw);
                self.mem_cache.insert(key, Arc::clone(&arc));
                self.cache_hits.fetch_add(1, Ordering::Relaxed);
                return Ok(arc);
            }
        }

        // Miss -> dispatch to headless wgpu GPU worker pool
        let mesh: ChunkMesh = self.gpu.bake_chunk(coord, seed).await?;
        let tris = mesh.triangle_count() as u64;
        let bake_us = mesh.bake_us as u64;
        let encoded = mesh.encode_binary();

        self.gpu_bakes.fetch_add(1, Ordering::Relaxed);
        self.total_bake_us.fetch_add(bake_us, Ordering::Relaxed);
        self.total_triangles.fetch_add(tris, Ordering::Relaxed);

        // Persist compressed blob asynchronously to NVMe disk cache
        let disk_bytes = encoded.clone();
        tokio::spawn(async move {
            if let Ok(z) = zstd::encode_all(&disk_bytes[..], 3) {
                let _ = tokio::fs::write(&disk_path, z).await;
            }
        });

        let arc = Arc::new(encoded);
        self.mem_cache.insert(key, Arc::clone(&arc));
        Ok(arc)
    }

    fn snapshot_stats(&self) -> ServerStats {
        let bakes = self.gpu_bakes.load(Ordering::Relaxed);
        let hits = self.cache_hits.load(Ordering::Relaxed);
        let total_us = self.total_bake_us.load(Ordering::Relaxed);
        let avg_gpu_ms = if bakes > 0 {
            (total_us as f64 / bakes as f64) / 1000.0
        } else {
            0.0
        };
        ServerStats {
            adapter: self.gpu.adapter_name().to_string(),
            backend: self.gpu.backend_name().to_string(),
            worker_slots: self.gpu.worker_count(),
            cached_chunks: self.mem_cache.len(),
            gpu_bakes: bakes,
            cache_hits: hits,
            avg_gpu_ms,
            total_triangles: self.total_triangles.load(Ordering::Relaxed),
        }
    }
}

#[derive(Debug, Serialize)]
struct ServerStats {
    adapter: String,
    backend: String,
    worker_slots: usize,
    cached_chunks: usize,
    gpu_bakes: u64,
    cache_hits: u64,
    avg_gpu_ms: f64,
    total_triangles: u64,
}

#[derive(Debug, Deserialize)]
struct BatchBakeRequest {
    #[serde(default = "default_seed")]
    seed: u32,
    #[serde(default = "default_batch_radius")]
    radius: i32,
    #[serde(default)]
    lod: u32,
    #[serde(default)]
    center: [i32; 3],
}

fn default_seed() -> u32 {
    42
}
fn default_batch_radius() -> i32 {
    3
}

#[derive(Debug, Serialize)]
struct BatchBakeResponse {
    chunks_baked: usize,
    elapsed_ms: f64,
    chunks_per_sec: f64,
    stats: ServerStats,
}

#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum ClientMessage {
    Pose(CameraPose),
    ClearClientCache,
}

#[derive(Debug, Serialize)]
#[serde(tag = "type", rename_all = "snake_case")]
enum ServerMessage {
    Stats(ServerStats),
    Evict { coords: Vec<ChunkCoord> },
}

async fn api_stats(State(state): State<Arc<AppState>>) -> Json<ServerStats> {
    Json(state.snapshot_stats())
}

/// Offline Batch World Baker endpoint — fans out a 3D region of chunks across
/// all headless `wgpu` worker slots concurrently and caches them to NVMe.
async fn api_batch_bake(
    State(state): State<Arc<AppState>>,
    Json(req): Json<BatchBakeRequest>,
) -> Json<BatchBakeResponse> {
    let start = Instant::now();
    let r = req.radius.clamp(1, 5);
    let mut coords = Vec::new();
    for cx in (req.center[0] - r)..=(req.center[0] + r) {
        for cy in -1..=2 {
            for cz in (req.center[2] - r)..=(req.center[2] + r) {
                coords.push(ChunkCoord::new(cx, cy, cz, req.lod.min(2)));
            }
        }
    }
    let total = coords.len();
    info!(
        seed = req.seed,
        lod = req.lod,
        chunks = total,
        "Starting GPU batch region bake"
    );

    let mut handles = Vec::with_capacity(total);
    for coord in coords {
        let st = Arc::clone(&state);
        let seed = req.seed;
        handles.push(tokio::spawn(async move {
            let _ = st.get_or_bake_chunk(coord, seed).await;
        }));
    }
    for h in handles {
        let _ = h.await;
    }

    let elapsed_ms = start.elapsed().as_secs_f64() * 1000.0;
    let chunks_per_sec = if elapsed_ms > 0.0 {
        total as f64 / (elapsed_ms / 1000.0)
    } else {
        0.0
    };
    info!(
        chunks = total,
        elapsed_ms = format!("{elapsed_ms:.1}"),
        chunks_per_sec = format!("{chunks_per_sec:.1}"),
        "Batch bake complete"
    );

    Json(BatchBakeResponse {
        chunks_baked: total,
        elapsed_ms,
        chunks_per_sec,
        stats: state.snapshot_stats(),
    })
}

async fn ws_handler(
    ws: WebSocketUpgrade,
    State(state): State<Arc<AppState>>,
) -> impl IntoResponse {
    ws.on_upgrade(move |socket| handle_ws_client(socket, state))
}

async fn handle_ws_client(socket: WebSocket, state: Arc<AppState>) {
    let (mut ws_tx, mut ws_rx) = socket.split();
    let (out_tx, mut out_rx) = mpsc::channel::<Message>(64);

    // Send initial GPU adapter stats immediately
    if let Ok(txt) = serde_json::to_string(&ServerMessage::Stats(state.snapshot_stats())) {
        let _ = out_tx.send(Message::Text(txt.into())).await;
    }

    // Writer task forwarding binary chunks & telemetry JSON to client
    let writer = tokio::spawn(async move {
        while let Some(msg) = out_rx.recv().await {
            if ws_tx.send(msg).await.is_err() {
                break;
            }
        }
    });

    // Latest camera pose channel (coalesces rapid camera updates so stale poses are skipped!)
    let (pose_tx, mut pose_rx) = watch::channel::<Option<CameraPose>>(None);
    let (clear_tx, mut clear_rx) = mpsc::channel::<()>(4);

    // Spatial interest + priority streaming loop
    let stream_state = Arc::clone(&state);
    let stream_out = out_tx.clone();
    let streamer = tokio::spawn(async move {
        let mut client_has: HashSet<(ChunkCoord, u32)> = HashSet::new();
        let mut last_stats = Instant::now();

        loop {
            tokio::select! {
                _ = clear_rx.recv() => {
                    client_has.clear();
                }
                changed = pose_rx.changed() => {
                    if changed.is_err() {
                        break;
                    }
                    let Some(pose) = pose_rx.borrow_and_update().clone() else {
                        continue;
                    };
                    let seed = pose.seed;
                    let desired = compute_desired_chunks(&pose);
                    let desired_set: HashSet<(ChunkCoord, u32)> =
                        desired.iter().map(|&c| (c, seed)).collect();

                    // Tell client to evict stale chunks outside view / replaced LODs
                    let to_evict: Vec<ChunkCoord> = client_has
                        .iter()
                        .filter(|k| !desired_set.contains(k))
                        .map(|(c, _)| *c)
                        .collect();
                    if !to_evict.is_empty() {
                        client_has.retain(|k| desired_set.contains(k));
                        if let Ok(txt) = serde_json::to_string(&ServerMessage::Evict { coords: to_evict }) {
                            if stream_out.send(Message::Text(txt.into())).await.is_err() {
                                break;
                            }
                        }
                    }

                    // Stream missing chunks in strict priority order, cancelling mid-batch if pose changes!
                    for coord in desired {
                        if pose_rx.has_changed().unwrap_or(false) {
                            break;
                        }
                        let key = (coord, seed);
                        if client_has.contains(&key) {
                            continue;
                        }
                        match stream_state.get_or_bake_chunk(coord, seed).await {
                            Ok(bytes) => {
                                if stream_out.send(Message::Binary((*bytes).clone().into())).await.is_err() {
                                    return;
                                }
                                client_has.insert(key);
                            }
                            Err(err) => {
                                warn!(?coord, %err, "Failed to bake chunk");
                            }
                        }

                        if last_stats.elapsed().as_millis() >= 250 {
                            last_stats = Instant::now();
                            if let Ok(txt) = serde_json::to_string(&ServerMessage::Stats(stream_state.snapshot_stats())) {
                                let _ = stream_out.try_send(Message::Text(txt.into()));
                            }
                        }
                    }

                    // Push updated telemetry once stream batch settles
                    if let Ok(txt) = serde_json::to_string(&ServerMessage::Stats(stream_state.snapshot_stats())) {
                        let _ = stream_out.try_send(Message::Text(txt.into()));
                    }
                }
            }
        }
    });

    // Reader loop from WebSocket client
    while let Some(Ok(msg)) = ws_rx.next().await {
        match msg {
            Message::Text(txt) => match serde_json::from_str::<ClientMessage>(&txt) {
                Ok(ClientMessage::Pose(pose)) => {
                    let _ = pose_tx.send(Some(pose));
                }
                Ok(ClientMessage::ClearClientCache) => {
                    let _ = clear_tx.try_send(());
                }
                Err(err) => {
                    warn!(%err, "Invalid client WS JSON message");
                }
            },
            Message::Close(_) => break,
            _ => {}
        }
    }

    drop(pose_tx);
    streamer.abort();
    writer.abort();
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "worldmill_server=info,worldmill_gpu=info".into()),
        )
        .init();

    let cache_dir = PathBuf::from(".worldmill-cache");
    tokio::fs::create_dir_all(&cache_dir).await?;

    let gpu = Arc::new(GpuWorkerPool::new(4).await?);
    let state = Arc::new(AppState {
        gpu,
        mem_cache: DashMap::new(),
        cache_dir,
        gpu_bakes: AtomicU64::new(0),
        cache_hits: AtomicU64::new(0),
        total_bake_us: AtomicU64::new(0),
        total_triangles: AtomicU64::new(0),
    });

    let app = Router::new()
        .route("/ws", get(ws_handler))
        .route("/api/stats", get(api_stats))
        .route("/api/batch/bake", post(api_batch_bake))
        .layer(CorsLayer::permissive())
        .with_state(state);

    let addr: SocketAddr = std::env::var("WORLDMILL_BIND")
        .unwrap_or_else(|_| "127.0.0.1:8080".to_string())
        .parse()?;

    info!(%addr, "WorldMill Rust + wgpu server listening");
    let listener = tokio::net::TcpListener::bind(addr).await?;
    axum::serve(listener, app).await?;
    Ok(())
}
