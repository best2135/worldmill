mod cache;
mod jobs;
mod singleflight;

use std::collections::HashSet;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Instant;

use anyhow::Result;
use axum::body::Bytes;
use axum::extract::ws::{Message, WebSocket, WebSocketUpgrade};
use axum::extract::{Path as AxumPath, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use tokio::sync::{Semaphore, mpsc, watch};
use tower_http::cors::CorsLayer;
use tower_http::services::{ServeDir, ServeFile};
use tracing::{info, warn};
use worldmill_core::{CameraPose, ChunkCoord, ChunkMesh, compute_desired_chunks};
use worldmill_gpu::GpuWorkerPool;

use crate::cache::LruCache;
use crate::jobs::{JobRegistry, JobSnapshot, JobStatus, run_job};
use crate::singleflight::SingleFlight;

type ChunkKey = (ChunkCoord, u32);

const DEFAULT_MAX_MEM_CHUNKS: usize = 1024;
const MAX_RETAINED_JOBS: usize = 256;

struct AppState {
    gpu: Arc<GpuWorkerPool>,
    /// Bounded LRU cache of encoded binary chunks (ready for zero-copy WebSocket send).
    mem_cache: LruCache<ChunkKey, Bytes>,
    /// Coalesces concurrent disk loads / GPU bakes of the same `(coord, seed)`.
    inflight: SingleFlight<ChunkKey, Bytes>,
    /// NVMe Zstd-compressed disk cache directory.
    cache_dir: PathBuf,
    /// Bounds concurrent batch chunk bakes so `/ws` streaming keeps GPU slots.
    batch_permits: Arc<Semaphore>,
    jobs: JobRegistry,
    /// Telemetry counters.
    gpu_bakes: AtomicU64,
    cache_hits: AtomicU64,
    mem_hits: AtomicU64,
    disk_hits: AtomicU64,
    inflight_coalesced: AtomicU64,
    total_bake_us: AtomicU64,
    total_triangles: AtomicU64,
}

impl AppState {
    fn new(
        gpu: Arc<GpuWorkerPool>,
        cache_dir: PathBuf,
        max_mem_chunks: usize,
        batch_concurrency: usize,
    ) -> Self {
        Self {
            gpu,
            mem_cache: LruCache::new(max_mem_chunks),
            inflight: SingleFlight::new(),
            cache_dir,
            batch_permits: Arc::new(Semaphore::new(batch_concurrency.max(1))),
            jobs: JobRegistry::new(MAX_RETAINED_JOBS),
            gpu_bakes: AtomicU64::new(0),
            cache_hits: AtomicU64::new(0),
            mem_hits: AtomicU64::new(0),
            disk_hits: AtomicU64::new(0),
            inflight_coalesced: AtomicU64::new(0),
            total_bake_us: AtomicU64::new(0),
            total_triangles: AtomicU64::new(0),
        }
    }

    fn mem_lookup(&self, key: &ChunkKey) -> Option<Bytes> {
        let hit = self.mem_cache.get(key)?;
        self.cache_hits.fetch_add(1, Ordering::Relaxed);
        self.mem_hits.fetch_add(1, Ordering::Relaxed);
        Some(hit)
    }

    async fn get_or_bake_chunk(self: &Arc<Self>, coord: ChunkCoord, seed: u32) -> Result<Bytes> {
        let key = (coord, seed);
        if let Some(hit) = self.mem_lookup(&key) {
            return Ok(hit);
        }

        let st = Arc::clone(self);
        let (res, coalesced) = self
            .inflight
            .run(key, move || async move {
                st.load_or_bake(coord, seed)
                    .await
                    .map_err(|e| format!("{e:#}"))
            })
            .await;
        if coalesced {
            self.inflight_coalesced.fetch_add(1, Ordering::Relaxed);
        }
        res.map_err(anyhow::Error::msg)
    }

    /// Slow path, run at most once per key at a time (via single-flight):
    /// memory (re-check) -> NVMe disk cache -> headless GPU bake.
    async fn load_or_bake(&self, coord: ChunkCoord, seed: u32) -> Result<Bytes> {
        let key = (coord, seed);
        // A flight for this key may have completed between our first lookup
        // and becoming the leader.
        if let Some(hit) = self.mem_lookup(&key) {
            return Ok(hit);
        }

        // Check NVMe Zstd disk cache
        let disk_path = self.cache_dir.join(coord.cache_key(seed));
        if let Ok(compressed) = tokio::fs::read(&disk_path).await {
            if let Ok(mut raw) = zstd::decode_all(&compressed[..]) {
                // Mark cache_hit flag in header (byte 36..40)
                if raw.len() >= 40 {
                    raw[36..40].copy_from_slice(&1u32.to_le_bytes());
                }
                let bytes = Bytes::from(raw);
                self.mem_cache.insert(key, bytes.clone());
                self.cache_hits.fetch_add(1, Ordering::Relaxed);
                self.disk_hits.fetch_add(1, Ordering::Relaxed);
                return Ok(bytes);
            }
        }

        // Miss -> dispatch to headless wgpu GPU worker pool
        let mesh: ChunkMesh = self.gpu.bake_chunk(coord, seed).await?;
        let tris = mesh.triangle_count() as u64;
        let bake_us = mesh.bake_us as u64;
        let bytes = Bytes::from(mesh.encode_binary());

        self.gpu_bakes.fetch_add(1, Ordering::Relaxed);
        self.total_bake_us.fetch_add(bake_us, Ordering::Relaxed);
        self.total_triangles.fetch_add(tris, Ordering::Relaxed);

        // Persist compressed blob asynchronously to NVMe disk cache. Write to a
        // temp file and rename so readers never observe a partial blob.
        let disk_bytes = bytes.clone();
        tokio::spawn(async move {
            let Ok(z) = zstd::encode_all(&disk_bytes[..], 3) else {
                return;
            };
            let tmp = disk_path.with_extension("zst.tmp");
            if tokio::fs::write(&tmp, z).await.is_ok() {
                let _ = tokio::fs::rename(&tmp, &disk_path).await;
            }
        });

        self.mem_cache.insert(key, bytes.clone());
        Ok(bytes)
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
            mem_capacity: self.mem_cache.capacity(),
            mem_hits: self.mem_hits.load(Ordering::Relaxed),
            disk_hits: self.disk_hits.load(Ordering::Relaxed),
            mem_evictions: self.mem_cache.evictions(),
            inflight_bakes: self.inflight.in_flight(),
            inflight_coalesced: self.inflight_coalesced.load(Ordering::Relaxed),
            batch_jobs_active: self.jobs.active_count(),
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
    /// Memory + disk hits (unchanged semantics).
    cache_hits: u64,
    avg_gpu_ms: f64,
    total_triangles: u64,
    // ---- Additive fields (Phase 2) ----
    mem_capacity: usize,
    mem_hits: u64,
    disk_hits: u64,
    mem_evictions: u64,
    inflight_bakes: usize,
    inflight_coalesced: u64,
    batch_jobs_active: usize,
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

impl BatchBakeRequest {
    fn lod(&self) -> u32 {
        self.lod.min(2)
    }

    /// The 3D region of chunks covered by this request.
    fn region_coords(&self) -> Vec<ChunkCoord> {
        let r = self.radius.clamp(1, 5);
        let lod = self.lod();
        let mut coords = Vec::new();
        for cx in (self.center[0] - r)..=(self.center[0] + r) {
            for cy in -1..=2 {
                for cz in (self.center[2] - r)..=(self.center[2] + r) {
                    coords.push(ChunkCoord::new(cx, cy, cz, lod));
                }
            }
        }
        coords
    }
}

#[derive(Debug, Serialize)]
struct BatchBakeResponse {
    chunks_baked: usize,
    elapsed_ms: f64,
    chunks_per_sec: f64,
    stats: ServerStats,
}

#[derive(Debug, Serialize)]
struct JobCreatedResponse {
    job_id: u64,
    total_chunks: usize,
    status: JobStatus,
}

#[derive(Debug, Serialize)]
struct JobListResponse {
    jobs: Vec<JobSnapshot>,
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
/// the headless `wgpu` worker slots (bounded by the batch semaphore) and caches
/// them to NVMe. Synchronous: responds once the whole region is baked.
async fn api_batch_bake(
    State(state): State<Arc<AppState>>,
    Json(req): Json<BatchBakeRequest>,
) -> Json<BatchBakeResponse> {
    let start = Instant::now();
    let coords = req.region_coords();
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
            let Ok(_permit) = st.batch_permits.acquire().await else {
                return;
            };
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

/// `POST /api/batch/jobs` — enqueue a background batch bake and return at once.
async fn api_create_job(
    State(state): State<Arc<AppState>>,
    Json(req): Json<BatchBakeRequest>,
) -> (StatusCode, Json<JobCreatedResponse>) {
    let coords = req.region_coords();
    let seed = req.seed;
    let job = state.jobs.create(seed, req.lod(), coords.len());
    let resp = JobCreatedResponse {
        job_id: job.id,
        total_chunks: job.total_chunks,
        status: job.status(),
    };
    info!(job_id = job.id, seed, chunks = coords.len(), "Queued background batch job");

    let st = Arc::clone(&state);
    tokio::spawn(async move {
        let permits = Arc::clone(&st.batch_permits);
        let job_id = job.id;
        let bake_state = Arc::clone(&st);
        let status = run_job(job, coords, permits, move |coord| {
            let st = Arc::clone(&bake_state);
            async move {
                st.get_or_bake_chunk(coord, seed)
                    .await
                    .map(|_| ())
                    .map_err(|e| format!("{e:#}"))
            }
        })
        .await;
        if let Some(job) = st.jobs.get(job_id) {
            let snap = job.snapshot();
            info!(
                job_id,
                ?status,
                completed = snap.completed_chunks,
                total = snap.total_chunks,
                elapsed_ms = format!("{:.1}", snap.elapsed_ms),
                "Background batch job finished"
            );
        }
    });

    (StatusCode::ACCEPTED, Json(resp))
}

fn job_not_found(id: u64) -> Response {
    (
        StatusCode::NOT_FOUND,
        Json(serde_json::json!({ "error": format!("batch job {id} not found") })),
    )
        .into_response()
}

/// `GET /api/batch/jobs` — list retained jobs, newest first.
async fn api_list_jobs(State(state): State<Arc<AppState>>) -> Json<JobListResponse> {
    Json(JobListResponse {
        jobs: state.jobs.list().iter().map(|j| j.snapshot()).collect(),
    })
}

/// `GET /api/batch/jobs/{id}` — live job progress.
async fn api_get_job(State(state): State<Arc<AppState>>, AxumPath(id): AxumPath<u64>) -> Response {
    match state.jobs.get(id) {
        Some(job) => Json(job.snapshot()).into_response(),
        None => job_not_found(id),
    }
}

/// `DELETE /api/batch/jobs/{id}` — cancel a queued/running job (idempotent).
async fn api_cancel_job(
    State(state): State<Arc<AppState>>,
    AxumPath(id): AxumPath<u64>,
) -> Response {
    match state.jobs.get(id) {
        Some(job) => {
            let status = job.cancel();
            info!(job_id = id, ?status, "Cancel requested for batch job");
            Json(job.snapshot()).into_response()
        }
        None => job_not_found(id),
    }
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
                                // `Bytes` clone is a refcount bump, not a copy.
                                if stream_out.send(Message::Binary(bytes)).await.is_err() {
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

fn env_usize(name: &str) -> Option<usize> {
    let raw = std::env::var(name).ok()?;
    match raw.trim().parse::<usize>() {
        Ok(v) if v > 0 => Some(v),
        _ => {
            warn!(var = name, value = %raw, "Ignoring invalid positive integer env var");
            None
        }
    }
}

/// Locate the built WebGPU frontend: `WORLDMILL_WEB_DIST`, then `./web/dist`,
/// then `web/dist` relative to the workspace root.
fn find_web_dist() -> Option<PathBuf> {
    let mut candidates = Vec::new();
    if let Ok(p) = std::env::var("WORLDMILL_WEB_DIST") {
        candidates.push(PathBuf::from(p));
    }
    candidates.push(PathBuf::from("web/dist"));
    candidates.push(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../web/dist"));
    candidates
        .into_iter()
        .find(|p| p.join("index.html").is_file())
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
    let max_mem_chunks = env_usize("WORLDMILL_MAX_MEM_CHUNKS").unwrap_or(DEFAULT_MAX_MEM_CHUNKS);
    // Leave at least half the GPU worker slots free for real-time `/ws` streaming.
    let batch_concurrency = env_usize("WORLDMILL_BATCH_CONCURRENCY")
        .unwrap_or_else(|| (gpu.worker_count() / 2).max(1));
    info!(max_mem_chunks, batch_concurrency, "Cache & batch limits configured");

    let state = Arc::new(AppState::new(gpu, cache_dir, max_mem_chunks, batch_concurrency));

    let mut app = Router::new()
        .route("/ws", get(ws_handler))
        .route("/api/stats", get(api_stats))
        .route("/api/batch/bake", post(api_batch_bake))
        .route("/api/batch/jobs", post(api_create_job).get(api_list_jobs))
        .route("/api/batch/jobs/{id}", get(api_get_job).delete(api_cancel_job));

    if let Some(dist) = find_web_dist() {
        info!(dir = %dist.display(), "Serving static WebGPU frontend on /");
        let index = dist.join("index.html");
        // Unknown paths fall back to index.html (200) for client-side routing.
        app = app.fallback_service(ServeDir::new(&dist).fallback(ServeFile::new(index)));
    } else {
        info!("web/dist not found; static frontend disabled (run `npm run build` in web/)");
    }

    let app = app.layer(CorsLayer::permissive()).with_state(state);

    let addr: SocketAddr = std::env::var("WORLDMILL_BIND")
        .unwrap_or_else(|_| "127.0.0.1:8080".to_string())
        .parse()?;

    info!(%addr, "WorldMill Rust + wgpu server listening");
    let listener = tokio::net::TcpListener::bind(addr).await?;
    axum::serve(listener, app).await?;
    Ok(())
}
