use std::sync::Arc;
use std::time::Instant;

use anyhow::{Context, Result, anyhow};
use bytemuck::{Pod, Zeroable};
use tokio::sync::{Mutex, Semaphore};
use tracing::info;
use worldmill_core::{CELL_GRID_DIM, CHUNK_CELLS, ChunkCoord, ChunkMesh, DENSITY_GRID_DIM, Vertex};

const MAX_VERTICES_PER_CHUNK: u32 = CELL_GRID_DIM * CELL_GRID_DIM * CELL_GRID_DIM; // 15,625
const MAX_INDICES_PER_CHUNK: u32 = MAX_VERTICES_PER_CHUNK * 12; // ~187,500 indices worst-case

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
    _pad0: u32,
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
    vertices_staging: wgpu::Buffer,
    indices_staging: wgpu::Buffer,
    bind_group: wgpu::BindGroup,
}

/// Headless `wgpu` GPU worker pool that executes 3-pass Surface Nets compute
/// shaders concurrently across pre-allocated VRAM worker slots.
pub struct GpuWorkerPool {
    device: wgpu::Device,
    queue: wgpu::Queue,
    adapter_name: String,
    backend_name: String,
    eval_density_pipeline: wgpu::ComputePipeline,
    build_vertices_pipeline: wgpu::ComputePipeline,
    emit_indices_pipeline: wgpu::ComputePipeline,
    slots: Vec<Mutex<WorkerSlot>>,
    permits: Arc<Semaphore>,
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

        let mut slots = Vec::with_capacity(worker_slots);
        for i in 0..worker_slots {
            slots.push(Mutex::new(create_worker_slot(&device, &bind_group_layout, i)));
        }

        Ok(Self {
            device,
            queue,
            adapter_name,
            backend_name,
            eval_density_pipeline,
            build_vertices_pipeline,
            emit_indices_pipeline,
            slots,
            permits: Arc::new(Semaphore::new(worker_slots)),
        })
    }

    pub fn adapter_name(&self) -> &str {
        &self.adapter_name
    }

    pub fn backend_name(&self) -> &str {
        &self.backend_name
    }

    pub fn worker_count(&self) -> usize {
        self.slots.len()
    }

    /// Bake one 3D chunk on the headless GPU using a pre-allocated worker slot.
    pub async fn bake_chunk(&self, coord: ChunkCoord, seed: u32) -> Result<ChunkMesh> {
        let _permit = self.permits.acquire().await?;

        // Acquire the first available pre-allocated VRAM slot
        let mut slot_guard = None;
        for slot in &self.slots {
            if let Ok(g) = slot.try_lock() {
                slot_guard = Some(g);
                break;
            }
        }
        let slot = match slot_guard {
            Some(g) => g,
            None => self.slots[0].lock().await,
        };

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
            _pad0: 0,
            _pad1: 0,
        };

        self.queue
            .write_buffer(&slot.uniform_buf, 0, bytemuck::bytes_of(&uniforms));
        let zero_counters = GpuCounters {
            vertex_count: 0,
            index_count: 0,
        };
        self.queue
            .write_buffer(&slot.counters_buf, 0, bytemuck::bytes_of(&zero_counters));

        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("bake-chunk-encoder"),
            });

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

        let counters: GpuCounters = {
            let slice = slot.counters_staging.slice(..);
            map_slice_async(&self.device, slice).await?;
            let data = slice.get_mapped_range();
            let c = *bytemuck::from_bytes::<GpuCounters>(&data);
            drop(data);
            slot.counters_staging.unmap();
            c
        };

        let v_count = counters.vertex_count.min(MAX_VERTICES_PER_CHUNK) as usize;
        let i_count = counters.index_count.min(MAX_INDICES_PER_CHUNK) as usize;

        if v_count == 0 || i_count == 0 {
            let bake_us = start.elapsed().as_micros().min(u32::MAX as u128) as u32;
            return Ok(ChunkMesh {
                coord,
                seed,
                vertices: Vec::new(),
                indices: Vec::new(),
                bake_us,
                cache_hit: false,
            });
        }

        // Copy only the exact produced vertex & index slices (fast DMA!)
        let v_bytes = (v_count * std::mem::size_of::<Vertex>()) as u64;
        let i_bytes = (i_count * std::mem::size_of::<u32>()) as u64;

        let mut copy_enc = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("readback-mesh-encoder"),
            });
        copy_enc.copy_buffer_to_buffer(&slot.vertices_buf, 0, &slot.vertices_staging, 0, v_bytes);
        copy_enc.copy_buffer_to_buffer(&slot.indices_buf, 0, &slot.indices_staging, 0, i_bytes);
        self.queue.submit(Some(copy_enc.finish()));

        let v_slice = slot.vertices_staging.slice(0..v_bytes);
        let i_slice = slot.indices_staging.slice(0..i_bytes);

        let (v_tx, v_rx) = tokio::sync::oneshot::channel();
        let (i_tx, i_rx) = tokio::sync::oneshot::channel();
        v_slice.map_async(wgpu::MapMode::Read, move |r| {
            let _ = v_tx.send(r);
        });
        i_slice.map_async(wgpu::MapMode::Read, move |r| {
            let _ = i_tx.send(r);
        });
        self.device.poll(wgpu::Maintain::Wait);
        v_rx.await??;
        i_rx.await??;

        let vertices = {
            let view = v_slice.get_mapped_range();
            bytemuck::cast_slice::<u8, Vertex>(&view).to_vec()
        };
        let indices = {
            let view = i_slice.get_mapped_range();
            bytemuck::cast_slice::<u8, u32>(&view).to_vec()
        };
        slot.vertices_staging.unmap();
        slot.indices_staging.unmap();

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
}

async fn map_slice_async(device: &wgpu::Device, slice: wgpu::BufferSlice<'_>) -> Result<()> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    slice.map_async(wgpu::MapMode::Read, move |res| {
        let _ = tx.send(res);
    });
    device.poll(wgpu::Maintain::Wait);
    rx.await??;
    Ok(())
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
    let density_bytes =
        (DENSITY_GRID_DIM * DENSITY_GRID_DIM * DENSITY_GRID_DIM) as u64 * std::mem::size_of::<f32>() as u64;
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
        usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });

    let counters_staging = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-counters-staging")),
        size: counter_bytes,
        usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    let vertices_staging = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-vertices-staging")),
        size: vertex_bytes,
        usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    let indices_staging = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(&format!("slot-{slot_idx}-indices-staging")),
        size: index_bytes,
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
        vertices_staging,
        indices_staging,
        bind_group,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn headless_gpu_bakes_terrain_mesh() {
        let pool = GpuWorkerPool::new(2).await.expect("GPU adapter required");
        println!("Testing on adapter: {} ({})", pool.adapter_name(), pool.backend_name());
        // Chunk (0, 0, 0, lod=0) covers y in [0, 24] which intersects procedural mountains
        let mesh = pool.bake_chunk(ChunkCoord::new(0, 0, 0, 0), 42).await.unwrap();
        println!(
            "Baked chunk (0,0,0) in {} us -> {} verts, {} tris",
            mesh.bake_us,
            mesh.vertices.len(),
            mesh.triangle_count()
        );
        assert!(!mesh.is_empty(), "expected terrain geometry at origin chunk");
        assert_eq!(mesh.indices.len() % 3, 0);
        for &idx in &mesh.indices {
            assert!((idx as usize) < mesh.vertices.len());
        }
    }
}
