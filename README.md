# WorldMill

**WorldMill** is a Rust + WebGPU procedural 3D world compiler and real-time spatial chunk streaming engine.

## What's Inside

- **`crates/worldmill-core`** — Shared chunk coordinates (`ChunkCoord`), LOD spatial partitioning (`compute_desired_chunks`), packed 32-byte GPU `Vertex` layout, and little-endian `WML1` binary wire codec.
- **`crates/worldmill-gpu`** — Headless `wgpu` GPU worker pool (pre-allocated VRAM worker slots, zero per-chunk GPU allocations) running a 3-pass WGSL compute pipeline (`shaders/surface_nets.wgsl`):
  1. `eval_density` — 3D domain-warped mountains, cliffs/overhangs, sky arches, and winding subterranean caves.
  2. `build_vertices` — Seamless Surface Nets vertex placement, central-difference gradient normals, 3-tap SDF ambient occlusion, and biome classification (basalt, sandstone, meadow, cliff granite, alpine snow, glowing cavern crystal).
  3. `emit_indices` — Outward-wound seam-free triangle emission across chunk borders.
- **`crates/worldmill-server`** — Tokio + Axum backend combining:
  - **Real-time spatial WebSocket streamer (`/ws`)**: coalesces camera pose updates, prioritizes near/in-front chunks, cancels stale batches mid-flight, and sends eviction lists.
  - **Offline GPU batch baker (`POST /api/batch/bake`)**: fans out a 3D chunk region across all headless `wgpu` worker slots concurrently.
  - **Two-tier chunk cache**: in-memory `DashMap` + Zstd-compressed NVMe disk cache (`.worldmill-cache/`).
  - **Live telemetry (`GET /api/stats`)**: GPU adapter, worker slots, average GPU bake time, cache hits, and triangle count.
- **`web/`** — Zero-framework Browser WebGPU 3D explorer (Vite + TypeScript) with procedural sky dome, ACES filmic tone-mapping, atmospheric fog, LOD tint debug toggle, and LRU GPU vertex/index buffer management.

## Architecture Diagrams

- [`diagrams/03-worldmill-3d-asset-streaming.svg`](diagrams/03-worldmill-3d-asset-streaming.svg) ([Excalidraw source](diagrams/03-worldmill-3d-asset-streaming.excalidraw))
- Earlier concept explorations:
  - [`diagrams/01-rayforge-path-tracing-farm.svg`](diagrams/01-rayforge-path-tracing-farm.svg)
  - [`diagrams/02-shaderstream-gpu-vfx-engine.svg`](diagrams/02-shaderstream-gpu-vfx-engine.svg)

## Quick Start

```bash
# 1. Start the Rust + headless wgpu backend (uses Vulkan / RTX GPU)
cargo run --release -p worldmill-server

# 2. In another terminal, start the WebGPU browser explorer
cd web && npm install && npm run dev
```

Then open **http://localhost:5173** in Chrome/Edge/Chromium (WebGPU enabled).

### Controls
- **Click & Drag** — Look around
- **W / A / S / D** — Fly
- **Space / E** and **Shift / Q** — Ascend / descend
- **F** — High-speed flight boost
- **Batch Bake Region (GPU)** — Dispatches a parallel GPU region bake to the Rust worker pool and caches to NVMe
- **LOD Tint** — Color-codes LOD 0 (`1m` voxels) vs LOD 1 (`2m` voxels) chunks live
