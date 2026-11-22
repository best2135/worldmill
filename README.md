# WorldMill

**WorldMill** is a Rust + WebGPU procedural 3D world compiler and real-time spatial chunk streaming engine.

- **Headless `wgpu` GPU Workers (Rust / Vulkan):** Evaluate 3D procedural SDFs (mountains, overhangs, caves, arches) and extract smooth, seam-free meshes with **GPU Surface Nets** compute shaders.
- **Batch World Baker & NVMe Cache:** Pre-bakes world regions in parallel across GPU workers into a content-addressed binary chunk cache.
- **Real-Time Spatial Stream Server (Tokio / Axum / WebSocket):** Computes frustum + LOD priority from client camera pose, cancels stale work on fast movement, and streams compact binary mesh chunks.
- **Browser WebGPU 3D Explorer (Vite + TypeScript + WebGPU):** Renders streamed chunks at 60+ FPS with an LRU GPU buffer cache, procedural sky, atmospheric fog, biome shading, and vertex ambient occlusion.

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
