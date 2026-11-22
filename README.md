# WorldMill

**WorldMill** is a Rust + WebGPU procedural 3D world compiler and real-time spatial chunk streaming engine.

## Benchmarks (RTX 4070 Ti SUPER · Vulkan · 4 GPU Worker Slots)
- **4,642 chunks/sec** · **10.87M triangles/sec** (`37,464` seamless Surface Nets triangles across 16 concurrent chunks in `3.45 ms`)
- **Zero-copy `WML1` binary WebSocket streaming** directly into browser `GPUBuffer`s

## Architecture

- [`diagrams/04-worldmill-concrete-architecture.svg`](diagrams/04-worldmill-concrete-architecture.svg) ([Excalidraw source](diagrams/04-worldmill-concrete-architecture.excalidraw))
- Earlier concept explorations:
  - [`diagrams/01-rayforge-path-tracing-farm.svg`](diagrams/01-rayforge-path-tracing-farm.svg)
  - [`diagrams/02-shaderstream-gpu-vfx-engine.svg`](diagrams/02-shaderstream-gpu-vfx-engine.svg)
  - [`diagrams/03-worldmill-3d-asset-streaming.svg`](diagrams/03-worldmill-3d-asset-streaming.svg)

## Workspace Breakdown

- **`crates/worldmill-core`** — Shared `ChunkCoord` (`24³` cells: LOD 0 = `24m`, LOD 1 = `48m`), frustum/LOD priority planner (`compute_desired_chunks`), packed 32-byte GPU `Vertex`, and 40-byte header `WML1` binary codec.
- **`crates/worldmill-gpu`** — Headless `wgpu` GPU worker pool with FIFO slot leasing, non-blocking DMA readbacks, empty/below-bedrock fast short-circuit, and a 3-pass WGSL compute pipeline (`shaders/surface_nets.wgsl`):
  1. `eval_density` — 3D domain-warped alpine ridges, canyon terraces, rock arches, and open cavern entrances with bedrock floor at `y = -40`.
  2. `build_vertices` — Seamless Surface Nets vertices, continuous gradient normals, 3-tap SDF ambient occlusion, and 6 biome classes (basalt, sandstone, meadow, cliff granite, snow, bioluminescent crystal).
  3. `emit_indices` — Outward-wound seam-free triangles across chunk boundaries.
- **`crates/worldmill-server`** — Tokio + Axum backend:
  - **Spatial stream (`/ws`)**: `watch::channel` pose coalescing, frustum/LOD prioritization, mid-flight cancellation, zero-copy `Bytes` binary frames, and client eviction lists.
  - **Bounded LRU RAM cache + NVMe Zstd cache** (`cache.rs`, `.worldmill-cache/`): configurable via `WORLDMILL_MAX_MEM_CHUNKS` (default `1024`), atomic temp-and-rename disk writes.
  - **Single-flight deduplication** (`singleflight.rs`): coalesces simultaneous requests for the same `(ChunkCoord, seed)`.
  - **Cancellable async batch jobs** (`jobs.rs`): `POST /api/batch/jobs`, `GET /api/batch/jobs`, `GET /api/batch/jobs/{id}`, `DELETE /api/batch/jobs/{id}`, plus synchronous `POST /api/batch/bake` and `GET /api/stats`. Concurrency bounded via `WORLDMILL_BATCH_CONCURRENCY` so batch jobs never starve real-time `/ws` streaming.
  - **Static frontend serving**: automatically serves `web/dist` on `/` when built.
- **`web/`** — Zero-framework Browser WebGPU 3D Explorer (Vite + TypeScript):
  - Procedural sky dome + dynamic sunset-to-midday sun angle slider
  - PBR-inspired biome terrain shader with GPU-baked AO and aerial fog
  - Animated 5-octave procedural Fresnel water plane (`y = -6.0`)
  - Live 2D HiDPI spatial chunk-streaming minimap (LOD 0 emerald, LOD 1 amber, fresh GPU-bake cyan flash)
  - One-click camera presets (**Alpine Vista**, **Sky Arches**, **Crystal Caves**) and **LOD Tint** debug toggle

## Quick Start

### Option A: Single-command run (serves built WebGPU app at `http://127.0.0.1:8080`)
```bash
(cd web && npm install && npm run build)
cargo run --release -p worldmill-server
```
Open **http://127.0.0.1:8080** in Chrome/Edge/Chromium (WebGPU enabled).

### Option B: Dev mode with Vite hot-reload
```bash
# Terminal 1 — Rust + headless wgpu backend
cargo run --release -p worldmill-server

# Terminal 2 — Vite WebGPU dev server (proxies /ws and /api to :8080)
cd web && npm run dev
```
Open **http://localhost:5173**.

### Run Tests & GPU Benchmarks
```bash
cargo test --workspace -- --nocapture
```
