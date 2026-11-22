export interface ChunkCoord {
  cx: number;
  cy: number;
  cz: number;
  lod: number;
}

export interface DecodedChunk {
  coord: ChunkCoord;
  seed: number;
  vertexCount: number;
  indexCount: number;
  bakeUs: number;
  cacheHit: boolean;
  vertexBytes: Uint8Array<ArrayBuffer>;
  indexBytes: Uint8Array<ArrayBuffer>;
}

const CHUNK_HEADER_BYTES = 40;
const VERTEX_STRIDE_BYTES = 32;
const WML1_MAGIC = 0x314c4d57; // "WML1" little-endian

export function chunkKey(c: ChunkCoord): string {
  return `${c.lod}:${c.cx},${c.cy},${c.cz}`;
}

export function decodeChunkPacket(buffer: ArrayBuffer): DecodedChunk {
  if (buffer.byteLength < CHUNK_HEADER_BYTES) {
    throw new Error(`Chunk packet too small: ${buffer.byteLength}`);
  }
  const view = new DataView(buffer);
  const magic = view.getUint32(0, true);
  if (magic !== WML1_MAGIC) {
    throw new Error(`Unexpected chunk magic: 0x${magic.toString(16)}`);
  }

  const cx = view.getInt32(4, true);
  const cy = view.getInt32(8, true);
  const cz = view.getInt32(12, true);
  const lod = view.getUint32(16, true);
  const seed = view.getUint32(20, true);
  const vertexCount = view.getUint32(24, true);
  const indexCount = view.getUint32(28, true);
  const bakeUs = view.getUint32(32, true);
  const flags = view.getUint32(36, true);

  const vLen = vertexCount * VERTEX_STRIDE_BYTES;
  const iLen = indexCount * 4;
  const vertexBytes = new Uint8Array(buffer, CHUNK_HEADER_BYTES, vLen);
  const indexBytes = new Uint8Array(buffer, CHUNK_HEADER_BYTES + vLen, iLen);

  return {
    coord: { cx, cy, cz, lod },
    seed,
    vertexCount,
    indexCount,
    bakeUs,
    cacheHit: (flags & 1) !== 0,
    vertexBytes,
    indexBytes,
  };
}
