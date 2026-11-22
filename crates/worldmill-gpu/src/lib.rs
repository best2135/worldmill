use std::sync::{Arc, mpsc as poll_channel};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, anyhow};
use bytemuck::{Pod, Zeroable};
use tokio::sync::{Mutex, mpsc, oneshot};
use tracing::info;
use worldmill_core::{CELL_GRID_DIM, CHUNK_CELLS, ChunkCoord, ChunkMesh, DENSITY_GRID_DIM, Vertex};

const BEDROCK_Y: f32 = -40.0;
const MAX_VERTICES_PER_CHUNK: u32 = CELL_GRID_DIM * CELL_GRID_DIM * CELL_GRID_DIM; // 15,625
const MAX_INDICES_PER_CHUNK: u32 = 18 * CHUNK_CELLS.pow(3); // Three quads per owned cell

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct ChunkUniforms {
    origin: [f32; 3],
    voxel_size: f32,
    seed: u32,
    chunk_cells: u32,
    cell_grid_dim: u32,
    density_grid_dim: u32,
    max_vertices: u32,
    max_indices: u32,
    bedrock_y: f32,
    _pad1: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct GpuCounters {
    vertex_count: u32,
    index_count: u32,
}

/// Reusable pre-allocated GPU buffer set for a single concurrent worker slot
/// (zero per-chunk GPU allocations!).
struct WorkerSlot {
    uniform_buf: wgpu::Buffer,
    counters_buf: wgpu::Buffer,
    vertices_buf: wgpu::Buffer,
    indices_buf: wgpu::Buffer,
    counters_staging: wgpu::Buffer,
    mesh_staging: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
}

/// Headless `wgpu` GPU worker pool that executes 3-pass Surface Nets compute
/// shaders concurrently across pre-allocated VRAM worker slots.
pub struct GpuWorkerPool {
    adapter_name: String,
    backend_name: String,
    gpu: GpuContext,
    free_slots: Mutex<mpsc::UnboundedReceiver<usize>>,
}

struct GpuContext {
    poller: GpuPoller,
    device: wgpu::Device,
    queue: wgpu::Queue,
    eval_density_pipeline: wgpu::ComputePipeline,
    build_vertices_pipeline: wgpu::ComputePipeline,
    emit_indices_pipeline: wgpu::ComputePipeline,
    slots: Vec<WorkerSlot>,
    return_slot: mpsc::UnboundedSender<usize>,
}

struct SlotLease {
    return_slot: mpsc::UnboundedSender<usize>,
    index: usize,
}

impl Drop for SlotLease {
    fn drop(&mut self) {
        let _ = self.return_slot.send(self.index);
    }
}

struct MappedReadback {
    buffer: wgpu::Buffer,
    size: u64,
    // Keep the slot leased even if the caller drops its future during mapping.
    _lease: Arc<SlotLease>,
}

impl MappedReadback {
    fn data(&self) -> wgpu::BufferView<'_> {
        self.buffer.slice(..self.size).get_mapped_range()
    }
}

impl Drop for MappedReadback {
    fn drop(&mut self) {
        self.buffer.unmap();
    }
}

struct GpuPoller(Option<(poll_channel::SyncSender<()>, JoinHandle<()>)>);

impl GpuPoller {
    fn new(device: wgpu::Device) -> Result<Self> {
        let (wake, requests) = poll_channel::sync_channel(1);
        let thread = thread::Builder::new()
            .name("worldmill-gpu-poll".into())
            .spawn(move || {
                while requests.recv().is_ok() {
                    // A blocking wgpu poll can lock out concurrent submissions.
                    while !device.poll(wgpu::Maintain::Poll).is_queue_empty() {
                        thread::sleep(Duration::from_micros(50));
                    }
                }
            })
            .context("failed to start GPU polling thread")?;
        Ok(Self(Some((wake, thread))))
    }

    fn wake(&self) -> Result<()> {
        let (wake, _) = self.0.as_ref().expect("poller is alive");
        match wake.try_send(()) {
            Ok(()) | Err(poll_channel::TrySendError::Full(())) => Ok(()),
            Err(poll_channel::TrySendError::Disconnected(())) => {
                Err(anyhow!("GPU polling thread stopped"))
            }
        }
    }
}

impl Drop for GpuPoller {
    fn drop(&mut self) {
        if let Some((wake, thread)) = self.0.take() {
            drop(wake);
            let _ = thread.join();
        }
    }
}

impl GpuWorkerPool {
    pub async fn new(worker_slots: usize) -> Result<Self> {
        let worker_slots = worker_slots.clamp(1, 8);
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
            backends: wgpu::Backends::VULKAN | wgpu::Backends::PRIMARY,
            ..Default::default()
        });

        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                compatible_surface: None,
                force_fallback_adapter: false,
            })
            .await
            .ok_or_else(|| anyhow!("no suitable headless wgpu GPU adapter found"))?;

        let info = adapter.get_info();
        let adapter_name = info.name.clone();
        let backend_name = format!("{:?}", info.backend);
        info!(
            adapter = %adapter_name,
            backend = %backend_name,
            device_type = ?info.device_type,
            worker_slots,
            "Initialized headless wgpu GPU worker pool"
        );

        let (device, queue) = adapter
            .request_device(
                &wgpu::DeviceDescriptor {
                    label: Some("worldmill-headless-device"),
                    required_features: wgpu::Features::empty(),
                    required_limits: wgpu::Limits::default(),
                    memory_hints: wgpu::MemoryHints::Performance,
                },
                None,
            )
            .await
            .context("failed to request headless wgpu device")?;

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("surface-nets-wgsl"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/surface_nets.wgsl").into()),
        });

        let bind_group_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("surface-nets-bgl"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                storage_rw_entry(1),
                storage_rw_entry(2),
                storage_rw_entry(3),
                storage_rw_entry(4),
                storage_rw_entry(5),
            ],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("surface-nets-pipeline-layout"),
            bind_group_layouts: &[&bind_group_layout],
            push_constant_ranges: &[],
        });

        let make_pipeline = |entry: &str| {
            device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: Some(entry),
                layout: Some(&pipeline_layout),
                module: &shader,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                cache: None,
            })
        };

        let eval_density_pipeline = make_pipeline("eval_density");
        let build_vertices_pipeline = make_pipeline("build_vertices");
        let emit_indices_pipeline = make_pipeline("emit_indices");

        let (return_slot, free_slots) = mpsc::unbounded_channel();
        let slots = (0..worker_slots)
            .map(|index| {
                return_slot.send(index).expect("slot receiver is alive");
                create_worker_slot(&device, &bind_group_layout, index)
            })
            .collect();

        Ok(Self {
            adapter_name,
            backend_name,
            gpu: GpuContext {
                poller: GpuPoller::new(device.clone())?,
                device,
                queue,
                eval_density_pipeline,
                build_vertices_pipeline,
                emit_indices_pipeline,
                slots,
                return_slot,
            },
            free_slots: Mutex::new(free_slots),
        })
    }

    pub fn adapter_name(&self) -> &str {
        &self.adapter_name
    }

    pub fn backend_name(&self) -> &str {
        &self.backend_name
    }

    pub fn worker_count(&self) -> usize {
        self.gpu.slots.len()
    }

    /// Bake one 3D chunk on the headless GPU using a pre-allocated worker slot.
    pub async fn bake_chunk(&self, coord: ChunkCoord, seed: u32) -> Result<ChunkMesh> {
        let origin = coord.world_origin();
        let top_sample = origin.y + (DENSITY_GRID_DIM - 1) as f32 * coord.voxel_size();
        if top_sample < BEDROCK_Y {
            return Ok(ChunkMesh {
                coord,
                seed,
                vertices: Vec::new(),
                indices: Vec::new(),
                bake_us: 0,
                cache_hit: false,
            });
        }

        let index = self
            .free_slots
            .lock()
            .await
            .recv()
            .await
            .context("GPU slot queue closed")?;
        let lease = Arc::new(SlotLease {
            return_slot: self.gpu.return_slot.clone(),
            index,
        });
        self.gpu.bake(&lease, coord, seed).await
    }
}

impl GpuContext {
    async fn bake(
        &self,
        lease: &Arc<SlotLease>,
        coord: ChunkCoord,
        seed: u32,
    ) -> Result<ChunkMesh> {
        let slot = &self.slots[lease.index];
        let start = Instant::now();
        let origin = coord.world_origin();
        let uniforms = ChunkUniforms {
            origin: origin.to_array(),
            voxel_size: coord.voxel_size(),
            seed,
            chunk_cells: CHUNK_CELLS,
            cell_grid_dim: CELL_GRID_DIM,
            density_grid_dim: DENSITY_GRID_DIM,
            max_vertices: MAX_VERTICES_PER_CHUNK,
            max_indices: MAX_INDICES_PER_CHUNK,
            bedrock_y: BEDROCK_Y,
            _pad1: 0,
        };

        self.queue
            .write_buffer(&slot.uniform_buf, 0, bytemuck::bytes_of(&uniforms));

        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("bake-chunk-encoder"),
            });

        encoder.clear_buffer(&slot.counters_buf, 0, None);

        let wg_density = DENSITY_GRID_DIM.div_ceil(4);
        let wg_cells = CELL_GRID_DIM.div_ceil(4);

        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("surface-nets-3-pass"),
                timestamp_writes: None,
            });
            pass.set_bind_group(0, &slot.bind_group, &[]);

            pass.set_pipeline(&self.eval_density_pipeline);
            pass.dispatch_workgroups(wg_density, wg_density, wg_density);

            pass.set_pipeline(&self.build_vertices_pipeline);
            pass.dispatch_workgroups(wg_cells, wg_cells, wg_cells);

            pass.set_pipeline(&self.emit_indices_pipeline);
            pass.dispatch_workgroups(wg_cells, wg_cells, wg_cells);
        }

        // First read back just the 8-byte counters buffer
        encoder.copy_buffer_to_buffer(
            &slot.counters_buf,
            0,
            &slot.counters_staging,
            0,
            std::mem::size_of::<GpuCounters>() as u64,
        );
        self.queue.submit(Some(encoder.finish()));

        let counters = {
            let readback = self
                .map_read(
                    lease,
                    &slot.counters_staging,
                    std::mem::size_of::<GpuCounters>() as u64,
                )
                .await?;
            *bytemuck::from_bytes::<GpuCounters>(&readback.data())
        };

        let (vertices, indices) = if counters.vertex_count == 0 || counters.index_count == 0 {
            (Vec::new(), Vec::new())
        } else {
            self.read_mesh(lease, slot, counters).await?
        };
        let bake_us = start.elapsed().as_micros().min(u32::MAX as u128) as u32;
        Ok(ChunkMesh {
            coord,
            seed,
            vertices,
            indices,
            bake_us,
            cache_hit: false,
        })
    }

    async fn read_mesh(
        &self,
        lease: &Arc<SlotLease>,
        slot: &WorkerSlot,
        counters: GpuCounters,
    ) -> Result<(Vec<Vertex>, Vec<u32>)> {
        let v_bytes = u64::from(counters.vertex_count) * std::mem::size_of::<Vertex>() as u64;
        let i_bytes = u64::from(counters.index_count) * std::mem::size_of::<u32>() as u64;

        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("readback-mesh-encoder"),
            });
        encoder.copy_buffer_to_buffer(&slot.vertices_buf, 0, &slot.mesh_staging, 0, v_bytes);
        encoder.copy_buffer_to_buffer(&slot.indices_buf, 0, &slot.mesh_staging, v_bytes, i_bytes);
        self.queue.submit(Some(encoder.finish()));

        let readback = self
            .map_read(lease, &slot.mesh_staging, v_bytes + i_bytes)
            .await?;
        let data = readback.data();
        let (vertices, indices) = data.split_at(v_bytes as usize);
        Ok((
            bytemuck::cast_slice::<u8, Vertex>(vertices).to_vec(),
            bytemuck::cast_slice::<u8, u32>(indices).to_vec(),
        ))
    }

    async fn map_read(
        &self,
        lease: &Arc<SlotLease>,
        buffer: &wgpu::Buffer,
        size: u64,
    ) -> Result<MappedReadback> {
        let (tx, rx) = oneshot::channel();
        let mapped_buffer = buffer.clone();
        let lease = Arc::clone(lease);
        buffer
            .slice(..size)
            .map_async(wgpu::MapMode::Read, move |result| {
                let _ = tx.send(result.map(|()| MappedReadback {
                    buffer: mapped_buffer,
                    size,
                    _lease: lease,
                }));
            });
        self.poller.wake()?;
        Ok(rx.await??)
    }
}

fn storage_rw_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty: wgpu::BufferBindingType::Storage { read_only: false },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    }
}

fn create_worker_slot(
    device: &wgpu::Device,
    bgl: &wgpu::BindGroupLayout,
    slot_idx: usize,
) -> WorkerSlot {
    let density_bytes = (DENSITY_GRID_DIM * DENSITY_GRID_DIM * DENSITY_GRID_DIM) as u64
        * std::mem::size_of::<f32>() as u64;
    let cell_bytes =
        (CELL_GRID_DIM * CELL_GRID_DIM * CELL_GRID_DIM) as u64 * std::mem::size_of::<u32>() as u64;
    let vertex_bytes = MAX_VERTICES_PER_CHUNK as u64 * std::mem::size_of::<Vertex>() as u64;
    let index_bytes = MAX_INDICES_PER_CHUNK as u64 * std::mem::size_of::<u32>() as u64;
    let counter_bytes = std::mem::size_of::<GpuCounters>() as u64;

    let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-uniforms")),
        size: std::mem::size_of::<ChunkUniforms>() as u64,
        usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    let densities_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-densities")),
        size: density_bytes,
        usage: wgpu::BufferUsages::STORAGE,
        mapped_at_creation: false,
    });
    let cell_to_vert_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-cell-to-vert")),
        size: cell_bytes,
        usage: wgpu::BufferUsages::STORAGE,
        mapped_at_creation: false,
    });
    let vertices_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-vertices")),
        size: vertex_bytes,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let indices_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-indices")),
        size: index_bytes,
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let counters_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-counters")),
        size: counter_bytes,
        usage: wgpu::BufferUsages::STORAGE
            | wgpu::BufferUsages::COPY_SRC
            | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    let counters_staging = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-counters-staging")),
        size: counter_bytes,
        usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    let mesh_staging = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-mesh-staging")),
        size: vertex_bytes + index_bytes,
        usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some(&format!("slot-{slot_idx}-bg")),
        layout: bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: densities_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: cell_to_vert_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 3,
                resource: vertices_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 4,
                resource: indices_buf.as_entire_binding(),
            },
            wgpu::BindGroupEntry {
                binding: 5,
                resource: counters_buf.as_entire_binding(),
            },
        ],
    });

    WorkerSlot {
        uniform_buf,
        counters_buf,
        vertices_buf,
        indices_buf,
        counters_staging,
        mesh_staging,
        bind_group,
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::future::Future;
    use std::task::{Context, Waker};

    use tokio::task::JoinSet;

    use super::*;

    // Keep the benchmark isolated from other GPU tests in this process.
    static GPU_TEST_LOCK: Mutex<()> = Mutex::const_new(());

    fn assert_mesh_valid(mesh: &ChunkMesh) {
        assert_eq!(mesh.indices.len() % 3, 0);
        for &index in &mesh.indices {
            assert!((index as usize) < mesh.vertices.len());
        }
        for vertex in &mesh.vertices {
            assert!(vertex.position.iter().all(|p| p.is_finite()));
            let normal_length_squared: f32 = vertex.normal.iter().map(|n| n * n).sum();
            assert!((normal_length_squared - 1.0).abs() < 1e-3);
            assert!((0.18..=1.0).contains(&vertex.ao));
            assert!((0..=5).any(|id| vertex.biome == id as f32));
        }
    }

    #[tokio::test]
    async fn headless_gpu_bakes_terrain_mesh() {
        let _gpu = GPU_TEST_LOCK.lock().await;
        let pool = GpuWorkerPool::new(2).await.expect("GPU adapter required");
        println!(
            "Testing on adapter: {} ({})",
            pool.adapter_name(),
            pool.backend_name()
        );
        let mesh = pool
            .bake_chunk(ChunkCoord::new(0, 0, 0, 0), 42)
            .await
            .unwrap();
        println!(
            "Baked chunk (0,0,0) in {} us -> {} verts, {} tris",
            mesh.bake_us,
            mesh.vertices.len(),
            mesh.triangle_count()
        );
        assert!(
            !mesh.is_empty(),
            "expected terrain geometry at origin chunk"
        );
        assert_mesh_valid(&mesh);
    }

    fn boundary_edges(mesh: &ChunkMesh, face_x: f32) -> Vec<[[f32; 3]; 2]> {
        // Surface Nets vertices lie inside cells. The stitching edges use the
        // cell layer that starts at the nominal face x = 24 in both chunks.
        let in_shared_layer = |[x, y, z]: [f32; 3]| {
            (face_x..=face_x + 1.0).contains(&x)
                && (1.0..23.0).contains(&y)
                && (1.0..23.0).contains(&z)
        };
        let mut edge_uses = HashMap::new();
        let (triangles, remainder) = mesh.indices.as_chunks::<3>();
        assert!(remainder.is_empty());
        for triangle in triangles {
            for i in 0..3 {
                let a = triangle[i];
                let b = triangle[(i + 1) % 3];
                if [a, b]
                    .iter()
                    .all(|&index| in_shared_layer(mesh.vertices[index as usize].position))
                {
                    *edge_uses.entry([a.min(b), a.max(b)]).or_insert(0) += 1;
                }
            }
        }
        edge_uses
            .into_iter()
            .filter(|&(_, uses)| uses == 1)
            .map(|(edge, _)| edge.map(|index| mesh.vertices[index as usize].position))
            .collect()
    }

    #[tokio::test]
    async fn adjacent_lod0_chunks_match_at_shared_x_face() {
        let _gpu = GPU_TEST_LOCK.lock().await;
        let pool = GpuWorkerPool::new(2).await.expect("GPU adapter required");
        let (left, right) = tokio::try_join!(
            pool.bake_chunk(ChunkCoord::new(0, 0, 0, 0), 42),
            pool.bake_chunk(ChunkCoord::new(1, 0, 0, 0), 42),
        )
        .unwrap();

        let left_edges = boundary_edges(&left, 24.0);
        let mut right_edges = boundary_edges(&right, 24.0);
        assert!(!left_edges.is_empty(), "expected a terrain seam at x = 24");
        assert_eq!(left_edges.len(), right_edges.len());
        let same_position = |a: [f32; 3], b: [f32; 3]| {
            let distance_squared: f32 = a.iter().zip(b).map(|(a, b)| (a - b).powi(2)).sum();
            distance_squared <= 1e-6 // (1e-3)^2
        };
        for [a, b] in left_edges {
            let matching = right_edges.iter().position(|&[c, d]| {
                (same_position(a, c) && same_position(b, d))
                    || (same_position(a, d) && same_position(b, c))
            });
            right_edges.swap_remove(
                matching.unwrap_or_else(|| panic!("unmatched seam edge at x = 24: {a:?} to {b:?}")),
            );
        }
    }

    #[tokio::test]
    async fn empty_chunks_and_cancelled_readbacks_leave_the_slot_reusable() {
        let _gpu = GPU_TEST_LOCK.lock().await;
        let pool = GpuWorkerPool::new(1).await.expect("GPU adapter required");
        let terrain = ChunkCoord::new(0, 0, 0, 0);

        {
            let mut bake = std::pin::pin!(pool.bake_chunk(terrain, 42));
            // Submit work and drop the future at the first readback if it is still pending.
            let _ = bake.as_mut().poll(&mut Context::from_waker(Waker::noop()));
        }

        for coord in [ChunkCoord::new(0, -3, 0, 0), ChunkCoord::new(0, 8, 0, 0)] {
            let mesh = pool.bake_chunk(coord, 42).await.unwrap();
            assert!(
                mesh.vertices.is_empty() && mesh.indices.is_empty(),
                "{coord:?}"
            );
        }

        let mesh = pool.bake_chunk(terrain, 42).await.unwrap();
        assert!(!mesh.is_empty());
        assert_mesh_valid(&mesh);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn concurrent_gpu_throughput() {
        let _gpu = GPU_TEST_LOCK.lock().await;
        let pool = Arc::new(GpuWorkerPool::new(4).await.expect("GPU adapter required"));
        assert_eq!(pool.worker_count(), 4);

        // Warm up one meshed bake per slot before timing steady-state throughput.
        let terrain = ChunkCoord::new(0, 0, 0, 0);
        let (a, b, c, d) = tokio::try_join!(
            pool.bake_chunk(terrain, 42),
            pool.bake_chunk(terrain, 42),
            pool.bake_chunk(terrain, 42),
            pool.bake_chunk(terrain, 42),
        )
        .unwrap();
        assert!([a, b, c, d].iter().all(|mesh| !mesh.is_empty()));

        let mut coords = Vec::new();
        for z in -1..=0 {
            for x in -1..=2 {
                for y in -1..=0 {
                    coords.push(ChunkCoord::new(x, y, z, 0));
                }
            }
        }

        let chunk_count = coords.len();
        assert_eq!(chunk_count, 16);
        let mut tasks = JoinSet::new();
        let start = Instant::now();
        for coord in coords {
            let pool = Arc::clone(&pool);
            tasks.spawn(async move {
                let mesh = pool.bake_chunk(coord, 42).await?;
                Ok::<_, anyhow::Error>((coord, mesh))
            });
        }
        let mut completed = Vec::with_capacity(chunk_count);
        while let Some(result) = tasks.join_next().await {
            completed.push(result.unwrap().unwrap());
        }
        let elapsed = start.elapsed().as_secs_f64();

        assert_eq!(completed.len(), chunk_count);
        let mut triangles = 0;
        let mut nonempty = 0;
        for (coord, mesh) in completed {
            assert_eq!(mesh.coord, coord);
            assert_eq!(mesh.seed, 42);
            assert_mesh_valid(&mesh);
            triangles += mesh.triangle_count();
            nonempty += usize::from(!mesh.is_empty());
        }
        assert!(nonempty > 0, "benchmark must exercise mesh readback");
        println!(
            "{} ({}) | {} slots | {} chunks ({} meshed) | {:.3} ms | {:.3} chunks/sec | {:.3} triangles/sec ({} triangles)",
            pool.adapter_name(),
            pool.backend_name(),
            pool.worker_count(),
            chunk_count,
            nonempty,
            elapsed * 1_000.0,
            chunk_count as f64 / elapsed,
            triangles as f64 / elapsed,
            triangles
        );
    }
}
