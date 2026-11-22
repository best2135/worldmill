import "./style.css";
import type { Vec3 } from "./math";
import { SpatialMinimap } from "./minimap";
import { decodeChunkPacket, type ChunkCoord } from "./protocol";
import { WorldRenderer } from "./renderer";
import { WebGL2WorldRenderer } from "./renderer_webgl2";

type AnyRenderer = WorldRenderer | WebGL2WorldRenderer;

interface ServerStats {
  adapter: string;
  backend: string;
  worker_slots: number;
  cached_chunks: number;
  gpu_bakes: number;
  cache_hits: number;
  avg_gpu_ms: number;
  total_triangles: number;
}

interface CameraPreset {
  pos: Vec3;
  yaw: number;
  pitch: number;
}

const CAMERA_PRESETS: Record<string, CameraPreset> = {
  alpine: { pos: [12, 36, 48], yaw: -Math.PI * 0.5, pitch: -0.22 },
  arches: { pos: [40, 58, 10], yaw: -Math.PI * 0.72, pitch: -0.34 },
  caves: { pos: [0, -18, 0], yaw: -Math.PI * 0.35, pitch: -0.06 },
};

const canvas = document.getElementById("gpu-canvas") as HTMLCanvasElement;
const minimapCanvas = document.getElementById("minimap-canvas") as HTMLCanvasElement;
const errOverlay = document.getElementById("webgpu-error") as HTMLDivElement;
const connDot = document.getElementById("conn-dot") as HTMLSpanElement;
const fpsBadge = document.getElementById("fps-badge") as HTMLSpanElement;
const gpuAdapterEl = document.getElementById("gpu-adapter") as HTMLDivElement;
const mChunks = document.getElementById("m-chunks") as HTMLElement;
const mTris = document.getElementById("m-tris") as HTMLElement;
const mBake = document.getElementById("m-bake") as HTMLElement;
const mHits = document.getElementById("m-hits") as HTMLElement;
const posLine = document.getElementById("pos-line") as HTMLDivElement;
const seedInput = document.getElementById("seed-input") as HTMLInputElement;
const btnSeed = document.getElementById("btn-seed") as HTMLButtonElement;
const btnBatch = document.getElementById("btn-batch") as HTMLButtonElement;
const btnLodTint = document.getElementById("btn-lod-tint") as HTMLButtonElement;
const batchStatus = document.getElementById("batch-status") as HTMLDivElement;
const sunSlider = document.getElementById("sun-slider") as HTMLInputElement;
const sunLabel = document.getElementById("sun-label") as HTMLSpanElement;
const presetButtons = Array.from(document.querySelectorAll<HTMLButtonElement>(".preset-btn"));

const camera = {
  pos: [...CAMERA_PRESETS.alpine.pos] as Vec3,
  yaw: CAMERA_PRESETS.alpine.yaw,
  pitch: CAMERA_PRESETS.alpine.pitch,
  seed: 42,
};

const keys = new Set<string>();
let isDragging = false;
let lastMouseX = 0;
let lastMouseY = 0;

function forwardDir(): Vec3 {
  const cp = Math.cos(camera.pitch);
  return [Math.cos(camera.yaw) * cp, Math.sin(camera.pitch), Math.sin(camera.yaw) * cp];
}

function sunDirFromElevationDeg(deg: number): Vec3 {
  const elev = (deg * Math.PI) / 180;
  // Sweep sun azimuth smoothly from golden western horizon toward midday southeast
  const t = Math.max(0, Math.min(1, (deg - 4) / (82 - 4)));
  const azimuth = -0.35 + t * 1.0;
  const horiz = Math.cos(elev);
  return [Math.cos(azimuth) * horiz, Math.sin(elev), Math.sin(azimuth) * horiz];
}

function formatSunLabel(deg: number): string {
  if (deg <= 14) return `Sunset ${deg}°`;
  if (deg <= 28) return `Golden ${deg}°`;
  if (deg <= 58) return `Afternoon ${deg}°`;
  return `Midday ${deg}°`;
}

function setupInput() {
  window.addEventListener("keydown", (e) => {
    if (document.activeElement === seedInput) return;
    if (e.code === "Space" || e.code.startsWith("Arrow")) {
      if (document.activeElement !== sunSlider) {
        e.preventDefault();
      }
    }
    keys.add(e.code);
  });
  window.addEventListener("keyup", (e) => keys.delete(e.code));

  canvas.addEventListener("mousedown", (e) => {
    isDragging = true;
    lastMouseX = e.clientX;
    lastMouseY = e.clientY;
  });
  window.addEventListener("mouseup", () => {
    isDragging = false;
  });
  window.addEventListener("mousemove", (e) => {
    if (!isDragging && document.pointerLockElement !== canvas) return;
    const dx = document.pointerLockElement === canvas ? e.movementX : e.clientX - lastMouseX;
    const dy = document.pointerLockElement === canvas ? e.movementY : e.clientY - lastMouseY;
    lastMouseX = e.clientX;
    lastMouseY = e.clientY;
    camera.yaw += dx * 0.0042;
    camera.pitch = Math.max(-1.48, Math.min(1.48, camera.pitch - dy * 0.0042));
  });
}

function updateCamera(dt: number) {
  const dir = forwardDir();
  // Right = normalize(cross(dir, up))
  let rx = -dir[2];
  let rz = dir[0];
  const rlen = Math.hypot(rx, rz) || 1;
  rx /= rlen;
  rz /= rlen;

  const speed = (keys.has("KeyF") ? 72 : 24) * dt;
  if (keys.has("KeyW") || keys.has("ArrowUp")) {
    camera.pos[0] += dir[0] * speed;
    camera.pos[1] += dir[1] * speed;
    camera.pos[2] += dir[2] * speed;
  }
  if (keys.has("KeyS") || keys.has("ArrowDown")) {
    camera.pos[0] -= dir[0] * speed;
    camera.pos[1] -= dir[1] * speed;
    camera.pos[2] -= dir[2] * speed;
  }
  if (keys.has("KeyD") || keys.has("ArrowRight")) {
    camera.pos[0] += rx * speed;
    camera.pos[2] += rz * speed;
  }
  if (keys.has("KeyA") || keys.has("ArrowLeft")) {
    camera.pos[0] -= rx * speed;
    camera.pos[2] -= rz * speed;
  }
  if (keys.has("Space") || keys.has("KeyE")) {
    camera.pos[1] += speed;
  }
  if (keys.has("ShiftLeft") || keys.has("ShiftRight") || keys.has("KeyQ")) {
    camera.pos[1] -= speed;
  }
}

async function boot() {
  let renderer: AnyRenderer = new WorldRenderer(canvas);
  try {
    await renderer.init();
  } catch (webgpuErr) {
    console.warn("WebGPU unavailable, falling back to WebGL2:", webgpuErr);
    renderer = new WebGL2WorldRenderer(canvas);
    try {
      await renderer.init();
    } catch (glErr) {
      errOverlay.textContent =
        glErr instanceof Error ? glErr.message : String(glErr);
      errOverlay.classList.remove("hidden");
      return;
    }
  }

  const minimap = new SpatialMinimap(minimapCanvas);

  const applySunAngle = () => {
    const deg = Number(sunSlider.value) || 46;
    renderer.sunDir = sunDirFromElevationDeg(deg);
    sunLabel.textContent = formatSunLabel(deg);
  };
  applySunAngle();
  sunSlider.addEventListener("input", applySunAngle);

  setupInput();
  window.addEventListener("resize", () => {
    renderer.resize();
    minimap.syncSize();
  });

  let ws: WebSocket | null = null;
  let lastPoseSentAt = 0;
  let lastSentPos: Vec3 = [99999, 99999, 99999];
  let lastSentYaw = 999;

  function sendPose(force = false) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    const now = performance.now();
    const moved = Math.hypot(
      camera.pos[0] - lastSentPos[0],
      camera.pos[1] - lastSentPos[1],
      camera.pos[2] - lastSentPos[2],
    );
    const turned = Math.abs(camera.yaw - lastSentYaw);
    if (!force && now - lastPoseSentAt < 140) return;
    if (!force && moved < 2.0 && turned < 0.12) return;

    lastPoseSentAt = now;
    lastSentPos = [...camera.pos] as Vec3;
    lastSentYaw = camera.yaw;

    ws.send(
      JSON.stringify({
        type: "pose",
        pos: camera.pos,
        dir: forwardDir(),
        seed: camera.seed,
        near_radius: 2,
        far_radius: 4,
      }),
    );
  }

  function updateStatsHud(s: ServerStats) {
    gpuAdapterEl.textContent = `Server: ${s.adapter} (${s.backend}) · Client: ${renderer.backendName}`;
    mBake.textContent = `${s.avg_gpu_ms.toFixed(2)} ms`;
    mHits.textContent = s.cache_hits.toLocaleString();
  }

  function connectWs() {
    const proto = window.location.protocol === "https:" ? "wss" : "ws";
    const url = `${proto}://${window.location.host}/ws`;
    ws = new WebSocket(url);
    ws.binaryType = "arraybuffer";

    ws.onopen = () => {
      connDot.className = "dot ok";
      sendPose(true);
    };
    ws.onclose = () => {
      connDot.className = "dot err";
      setTimeout(connectWs, 1500);
    };
    ws.onmessage = (ev) => {
      if (ev.data instanceof ArrayBuffer) {
        const decoded = decodeChunkPacket(ev.data);
        if (decoded.seed === camera.seed) {
          renderer.upsertChunk(decoded);
          minimap.upsertChunk(decoded);
        }
        return;
      }
      if (typeof ev.data === "string") {
        const msg = JSON.parse(ev.data);
        if (msg.type === "stats") {
          updateStatsHud(msg as ServerStats);
        } else if (msg.type === "evict" && Array.isArray(msg.coords)) {
          const coords = msg.coords as ChunkCoord[];
          renderer.evictChunks(coords);
          minimap.evictChunks(coords);
        }
      }
    };
  }

  connectWs();

  for (const btn of presetButtons) {
    btn.addEventListener("click", () => {
      const key = btn.dataset.preset;
      const preset = key ? CAMERA_PRESETS[key] : undefined;
      if (!preset) return;
      camera.pos = [...preset.pos] as Vec3;
      camera.yaw = preset.yaw;
      camera.pitch = preset.pitch;
      for (const other of presetButtons) {
        other.classList.toggle("active", other === btn);
      }
      posLine.textContent = `Pos: ${camera.pos[0].toFixed(1)}, ${camera.pos[1].toFixed(1)}, ${camera.pos[2].toFixed(1)}`;
      sendPose(true);
      btn.blur();
    });
  }

  btnSeed.addEventListener("click", () => {
    const parsed = Math.max(1, parseInt(seedInput.value, 10) || 42);
    camera.seed = parsed;
    renderer.clearAllChunks();
    minimap.clear();
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: "clear_client_cache" }));
    }
    sendPose(true);
    btnSeed.blur();
  });

  btnLodTint.addEventListener("click", () => {
    renderer.lodTint = !renderer.lodTint;
    btnLodTint.classList.toggle("active", renderer.lodTint);
    btnLodTint.blur();
  });

  btnBatch.addEventListener("click", async () => {
    btnBatch.disabled = true;
    batchStatus.textContent = "Dispatching batch bake to Rust wgpu workers...";
    try {
      const cx = Math.floor(camera.pos[0] / 24);
      const cz = Math.floor(camera.pos[2] / 24);
      const res = await fetch("/api/batch/bake", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          seed: camera.seed,
          radius: 3,
          lod: 0,
          center: [cx, 0, cz],
        }),
      });
      const data = await res.json();
      batchStatus.textContent = `Baked ${data.chunks_baked} chunks in ${data.elapsed_ms.toFixed(0)} ms (${data.chunks_per_sec.toFixed(0)} chunks/s)`;
      if (data.stats) updateStatsHud(data.stats);
      sendPose(true);
    } catch (e) {
      batchStatus.textContent = `Batch bake failed: ${e}`;
    } finally {
      btnBatch.disabled = false;
      btnBatch.blur();
    }
  });

  let lastFrame = performance.now();
  let frameCount = 0;
  let fpsTimer = performance.now();

  function tick(now: number) {
    const dt = Math.min(0.05, (now - lastFrame) / 1000);
    lastFrame = now;

    updateCamera(dt);
    sendPose(false);
    renderer.render(camera.pos, forwardDir(), now / 1000);
    minimap.render(camera.pos, camera.yaw, renderer.getAspect(), now);

    frameCount++;
    if (now - fpsTimer >= 400) {
      const fps = Math.round((frameCount * 1000) / (now - fpsTimer));
      fpsBadge.textContent = `${fps} FPS`;
      mChunks.textContent = renderer.getChunkCount().toLocaleString();
      mTris.textContent = renderer.getTriangleCount().toLocaleString();
      posLine.textContent = `Pos: ${camera.pos[0].toFixed(1)}, ${camera.pos[1].toFixed(1)}, ${camera.pos[2].toFixed(1)}`;
      frameCount = 0;
      fpsTimer = now;
    }

    requestAnimationFrame(tick);
  }

  requestAnimationFrame(tick);
}

boot();
