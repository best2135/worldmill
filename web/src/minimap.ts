import type { Vec3 } from "./math";
import { chunkKey, type ChunkCoord, type DecodedChunk } from "./protocol";

interface TrackedChunk {
  coord: ChunkCoord;
  hasGeometry: boolean;
  freshBakeAt: number;
}

interface ColumnSummary {
  cx: number;
  cz: number;
  lod: number;
  hasGeometry: boolean;
  latestBakeAt: number;
}

const MINIMAP_SIZE = 160;
const WORLD_RADIUS_METERS = 230;
const FLASH_DURATION_MS = 1500;
const VERT_FOV_RAD = (62 * Math.PI) / 180;

export class SpatialMinimap {
  private ctx: CanvasRenderingContext2D;
  private chunks = new Map<string, TrackedChunk>();
  private dpr = 1;

  constructor(private canvas: HTMLCanvasElement) {
    const ctx = canvas.getContext("2d");
    if (!ctx) {
      throw new Error("Failed to acquire 2D context for spatial minimap.");
    }
    this.ctx = ctx;
    this.syncSize();
  }

  syncSize(): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const target = Math.round(MINIMAP_SIZE * dpr);
    if (this.canvas.width !== target || this.canvas.height !== target || this.dpr !== dpr) {
      this.dpr = dpr;
      this.canvas.width = target;
      this.canvas.height = target;
      this.canvas.style.width = `${MINIMAP_SIZE}px`;
      this.canvas.style.height = `${MINIMAP_SIZE}px`;
    }
  }

  upsertChunk(chunk: DecodedChunk, nowMs = performance.now()): void {
    const key = chunkKey(chunk.coord);
    this.chunks.set(key, {
      coord: chunk.coord,
      hasGeometry: chunk.indexCount > 0,
      freshBakeAt: chunk.cacheHit ? 0 : nowMs,
    });
  }

  evictChunks(coords: ChunkCoord[]): void {
    for (const c of coords) {
      this.chunks.delete(chunkKey(c));
    }
  }

  clear(): void {
    this.chunks.clear();
  }

  render(camPos: Vec3, yaw: number, aspect: number, nowMs: number): void {
    this.syncSize();
    const ctx = this.ctx;
    const size = MINIMAP_SIZE;
    const half = size * 0.5;
    const pxPerMeter = half / WORLD_RADIUS_METERS;

    ctx.save();
    ctx.scale(this.dpr, this.dpr);

    // Background
    ctx.clearRect(0, 0, size, size);
    ctx.fillStyle = "rgba(8, 13, 24, 0.92)";
    ctx.fillRect(0, 0, size, size);

    // Aggregate vertical cy chunks into XZ columns per LOD
    const columns = new Map<string, ColumnSummary>();
    for (const entry of this.chunks.values()) {
      const colKey = `${entry.coord.lod}:${entry.coord.cx},${entry.coord.cz}`;
      const existing = columns.get(colKey);
      if (!existing) {
        columns.set(colKey, {
          cx: entry.coord.cx,
          cz: entry.coord.cz,
          lod: entry.coord.lod,
          hasGeometry: entry.hasGeometry,
          latestBakeAt: entry.freshBakeAt,
        });
      } else {
        if (entry.hasGeometry) existing.hasGeometry = true;
        if (entry.freshBakeAt > existing.latestBakeAt) {
          existing.latestBakeAt = entry.freshBakeAt;
        }
      }
    }

    const lod1Cols: ColumnSummary[] = [];
    const lod0Cols: ColumnSummary[] = [];
    for (const col of columns.values()) {
      if (col.lod === 0) lod0Cols.push(col);
      else lod1Cols.push(col);
    }

    const drawColumns = (list: ColumnSummary[], spanMeters: number, isLod0: boolean) => {
      const cellPx = spanMeters * pxPerMeter;
      const drawSize = Math.max(2, cellPx - 1);
      for (const col of list) {
        const wx = col.cx * spanMeters;
        const wz = col.cz * spanMeters;
        const sx = half + (wx - camPos[0]) * pxPerMeter;
        const sy = half + (wz - camPos[2]) * pxPerMeter;
        if (sx + cellPx < 0 || sx > size || sy + cellPx < 0 || sy > size) continue;

        const bakeAge = col.latestBakeAt > 0 ? nowMs - col.latestBakeAt : Infinity;
        const flashT = bakeAge < FLASH_DURATION_MS ? 1 - bakeAge / FLASH_DURATION_MS : 0;

        if (isLod0) {
          ctx.fillStyle = col.hasGeometry
            ? "rgba(16, 185, 129, 0.56)"
            : "rgba(16, 185, 129, 0.22)";
          ctx.strokeStyle = "rgba(52, 211, 153, 0.55)";
        } else {
          ctx.fillStyle = col.hasGeometry
            ? "rgba(245, 158, 11, 0.44)"
            : "rgba(245, 158, 11, 0.16)";
          ctx.strokeStyle = "rgba(251, 191, 36, 0.42)";
        }

        ctx.lineWidth = 0.75;
        ctx.fillRect(sx + 0.5, sy + 0.5, drawSize, drawSize);
        ctx.strokeRect(sx + 0.5, sy + 0.5, drawSize, drawSize);

        if (flashT > 0) {
          const ease = flashT * flashT;
          ctx.fillStyle = `rgba(34, 211, 238, ${(0.72 * ease).toFixed(3)})`;
          ctx.fillRect(sx + 0.5, sy + 0.5, drawSize, drawSize);
          ctx.strokeStyle = `rgba(103, 232, 249, ${(0.95 * ease).toFixed(3)})`;
          ctx.lineWidth = 1.25;
          ctx.strokeRect(sx + 0.5, sy + 0.5, drawSize, drawSize);
        }
      }
    };

    // Draw outer LOD 1 first, then inner LOD 0 on top
    drawColumns(lod1Cols, 48, false);
    drawColumns(lod0Cols, 24, true);

    // Subtle distance rings (near ~96m, far ~192m) and crosshairs
    ctx.strokeStyle = "rgba(148, 163, 184, 0.18)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(half, 0);
    ctx.lineTo(half, size);
    ctx.moveTo(0, half);
    ctx.lineTo(size, half);
    ctx.stroke();

    ctx.setLineDash([2, 3]);
    for (const rMeters of [96, 192]) {
      ctx.beginPath();
      ctx.arc(half, half, rMeters * pxPerMeter, 0, Math.PI * 2);
      ctx.stroke();
    }
    ctx.setLineDash([]);

    // Live rotating view frustum cone
    const halfHFov = Math.atan(Math.tan(VERT_FOV_RAD * 0.5) * Math.max(0.6, aspect));
    const coneLen = 42;
    const coneGrad = ctx.createRadialGradient(half, half, 2, half, half, coneLen);
    coneGrad.addColorStop(0, "rgba(125, 211, 252, 0.42)");
    coneGrad.addColorStop(1, "rgba(56, 189, 248, 0.0)");

    ctx.fillStyle = coneGrad;
    ctx.beginPath();
    ctx.moveTo(half, half);
    ctx.arc(half, half, coneLen, yaw - halfHFov, yaw + halfHFov);
    ctx.closePath();
    ctx.fill();

    ctx.strokeStyle = "rgba(125, 211, 252, 0.45)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(half, half);
    ctx.lineTo(half + Math.cos(yaw - halfHFov) * coneLen, half + Math.sin(yaw - halfHFov) * coneLen);
    ctx.moveTo(half, half);
    ctx.lineTo(half + Math.cos(yaw + halfHFov) * coneLen, half + Math.sin(yaw + halfHFov) * coneLen);
    ctx.stroke();

    // Camera marker & directional arrow
    ctx.save();
    ctx.translate(half, half);
    ctx.rotate(yaw);

    ctx.fillStyle = "#f8fafc";
    ctx.strokeStyle = "#0ea5e9";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(8, 0);
    ctx.lineTo(-4.5, 4.5);
    ctx.lineTo(-2.2, 0);
    ctx.lineTo(-4.5, -4.5);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.restore();

    // Cardinal North indicator (-Z)
    ctx.fillStyle = "rgba(148, 163, 184, 0.85)";
    ctx.font = "600 9px ui-monospace, SFMono-Regular, monospace";
    ctx.textAlign = "center";
    ctx.textBaseline = "top";
    ctx.fillText("N", half, 3);

    ctx.restore();
  }
}
