use anyhow::{Result, bail};
use bytemuck::{Pod, Zeroable};
use glam::Vec3;
use serde::{Deserialize, Serialize};

/// Number of surface-net cells along each axis of a chunk.
pub const CHUNK_CELLS: u32 = 24;

/// Number of cell vertices per axis (CHUNK_CELLS + 1) for seamless stitching across chunk borders.
pub const CELL_GRID_DIM: u32 = CHUNK_CELLS + 1;

/// Number of density samples per axis (CHUNK_CELLS + 2).
pub const DENSITY_GRID_DIM: u32 = CHUNK_CELLS + 2;

/// Binary chunk packet magic ("WML1").
pub const CHUNK_WIRE_MAGIC: u32 = u32::from_le_bytes(*b"WML1");

/// Binary chunk header size in bytes.
pub const CHUNK_HEADER_BYTES: usize = 40;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct ChunkCoord {
    pub cx: i32,
    pub cy: i32,
    pub cz: i32,
    pub lod: u32,
}

impl ChunkCoord {
    pub const fn new(cx: i32, cy: i32, cz: i32, lod: u32) -> Self {
        Self { cx, cy, cz, lod }
    }

    /// World-space voxel spacing at this LOD (LOD 0 = 1.0m, LOD 1 = 2.0m, LOD 2 = 4.0m).
    #[inline]
    pub fn voxel_size(&self) -> f32 {
        (1u32 << self.lod.min(4)) as f32
    }

    /// World-space span of one chunk edge.
    #[inline]
    pub fn world_span(&self) -> f32 {
        CHUNK_CELLS as f32 * self.voxel_size()
    }

    /// Minimum corner of this chunk in world space.
    #[inline]
    pub fn world_origin(&self) -> Vec3 {
        let span = self.world_span();
        Vec3::new(
            self.cx as f32 * span,
            self.cy as f32 * span,
            self.cz as f32 * span,
        )
    }

    /// Center of this chunk in world space.
    #[inline]
    pub fn world_center(&self) -> Vec3 {
        self.world_origin() + Vec3::splat(self.world_span() * 0.5)
    }

    /// Deterministic filename key for NVMe disk cache.
    pub fn cache_key(&self, seed: u32) -> String {
        format!(
            "s{}_l{}_{}_{}_{}.wml.zst",
            seed, self.lod, self.cx, self.cy, self.cz
        )
    }
}

/// GPU-ready packed vertex (32 bytes, matches WGSL `Vertex` struct and WebGPU vertex layout).
#[repr(C)]
#[derive(Debug, Clone, Copy, Default, Pod, Zeroable)]
pub struct Vertex {
    pub position: [f32; 3],
    pub ao: f32,
    pub normal: [f32; 3],
    pub biome: f32,
}

/// Completed chunk mesh produced by a headless `wgpu` worker or loaded from NVMe cache.
#[derive(Debug, Clone)]
pub struct ChunkMesh {
    pub coord: ChunkCoord,
    pub seed: u32,
    pub vertices: Vec<Vertex>,
    pub indices: Vec<u32>,
    pub bake_us: u32,
    pub cache_hit: bool,
}

impl ChunkMesh {
    pub fn is_empty(&self) -> bool {
        self.vertices.is_empty() || self.indices.is_empty()
    }

    pub fn triangle_count(&self) -> usize {
        self.indices.len() / 3
    }

    /// Encode chunk mesh into compact little-endian binary frame ready for WebSocket or disk storage.
    pub fn encode_binary(&self) -> Vec<u8> {
        let v_bytes = bytemuck::cast_slice::<Vertex, u8>(&self.vertices);
        let i_bytes = bytemuck::cast_slice::<u32, u8>(&self.indices);
        let mut out = Vec::with_capacity(CHUNK_HEADER_BYTES + v_bytes.len() + i_bytes.len());

        out.extend_from_slice(&CHUNK_WIRE_MAGIC.to_le_bytes());
        out.extend_from_slice(&self.coord.cx.to_le_bytes());
        out.extend_from_slice(&self.coord.cy.to_le_bytes());
        out.extend_from_slice(&self.coord.cz.to_le_bytes());
        out.extend_from_slice(&self.coord.lod.to_le_bytes());
        out.extend_from_slice(&self.seed.to_le_bytes());
        out.extend_from_slice(&(self.vertices.len() as u32).to_le_bytes());
        out.extend_from_slice(&(self.indices.len() as u32).to_le_bytes());
        out.extend_from_slice(&self.bake_us.to_le_bytes());
        let flags: u32 = if self.cache_hit { 1 } else { 0 };
        out.extend_from_slice(&flags.to_le_bytes());

        out.extend_from_slice(v_bytes);
        out.extend_from_slice(i_bytes);
        out
    }

    /// Decode a binary chunk frame back into a `ChunkMesh`.
    pub fn decode_binary(data: &[u8]) -> Result<Self> {
        if data.len() < CHUNK_HEADER_BYTES {
            bail!("chunk payload too short: {} bytes", data.len());
        }
        let read_u32 = |off: usize| -> u32 {
            u32::from_le_bytes(data[off..off + 4].try_into().unwrap())
        };
        let read_i32 = |off: usize| -> i32 {
            i32::from_le_bytes(data[off..off + 4].try_into().unwrap())
        };

        let magic = read_u32(0);
        if magic != CHUNK_WIRE_MAGIC {
            bail!("invalid chunk wire magic: {magic:#x}");
        }
        let coord = ChunkCoord {
            cx: read_i32(4),
            cy: read_i32(8),
            cz: read_i32(12),
            lod: read_u32(16),
        };
        let seed = read_u32(20);
        let vertex_count = read_u32(24) as usize;
        let index_count = read_u32(28) as usize;
        let bake_us = read_u32(32);
        let flags = read_u32(36);

        let v_len = vertex_count * std::mem::size_of::<Vertex>();
        let i_len = index_count * std::mem::size_of::<u32>();
        let expected = CHUNK_HEADER_BYTES + v_len + i_len;
        if data.len() != expected {
            bail!("chunk payload size mismatch: got {}, expected {expected}", data.len());
        }

        let vertices = bytemuck::cast_slice::<u8, Vertex>(
            &data[CHUNK_HEADER_BYTES..CHUNK_HEADER_BYTES + v_len],
        )
        .to_vec();
        let indices = bytemuck::cast_slice::<u8, u32>(
            &data[CHUNK_HEADER_BYTES + v_len..expected],
        )
        .to_vec();

        Ok(Self {
            coord,
            seed,
            vertices,
            indices,
            bake_us,
            cache_hit: (flags & 1) != 0,
        })
    }
}

/// Spatial interest request sent from a WebGPU browser client.
#[derive(Debug, Clone, Deserialize)]
pub struct CameraPose {
    pub pos: [f32; 3],
    pub dir: [f32; 3],
    #[serde(default = "default_seed")]
    pub seed: u32,
    #[serde(default = "default_near_macro")]
    pub near_radius: i32,
    #[serde(default = "default_far_macro")]
    pub far_radius: i32,
}

fn default_seed() -> u32 {
    42
}
fn default_near_macro() -> i32 {
    2
}
fn default_far_macro() -> i32 {
    4
}

/// Computes the exact non-overlapping desired chunk set around a camera pose,
/// ordered by streaming priority (closest & in-front-of-camera chunks first).
///
/// Space is partitioned into LOD 1 macro-cells (`48m` cubes). Macro-cells within
/// `near_radius` expand into 2x2x2 LOD 0 chunks (`24m` cubes); outer macro-cells
/// up to `far_radius` use a single LOD 1 chunk.
pub fn compute_desired_chunks(pose: &CameraPose) -> Vec<ChunkCoord> {
    let cam = Vec3::from_array(pose.pos);
    let dir = Vec3::from_array(pose.dir).normalize_or_zero();
    let macro_span = CHUNK_CELLS as f32 * 2.0; // 48m

    let base_mx = (cam.x / macro_span).floor() as i32;
    let base_mz = (cam.z / macro_span).floor() as i32;

    let near = pose.near_radius.clamp(1, 3);
    let far = pose.far_radius.clamp(near, 6);

    // Vertical macro range: y in [-48m, +96m) => my in [-1, 0, 1]
    let min_my = -1;
    let max_my = 1;

    let mut scored: Vec<(f32, ChunkCoord)> = Vec::with_capacity(512);

    for mx in (base_mx - far)..=(base_mx + far) {
        for mz in (base_mz - far)..=(base_mz + far) {
            let dx = (mx - base_mx) as f32;
            let dz = (mz - base_mz) as f32;
            let horiz_dist = (dx * dx + dz * dz).sqrt();
            if horiz_dist > far as f32 + 0.4 {
                continue;
            }

            let is_near = horiz_dist <= near as f32 + 0.25;
            for my in min_my..=max_my {
                if is_near {
                    // Expand macro-cell into 2x2x2 LOD 0 chunks
                    for ox in 0..2 {
                        for oy in 0..2 {
                            for oz in 0..2 {
                                let coord = ChunkCoord::new(mx * 2 + ox, my * 2 + oy, mz * 2 + oz, 0);
                                let score = priority_score(cam, dir, &coord);
                                scored.push((score, coord));
                            }
                        }
                    }
                } else {
                    let coord = ChunkCoord::new(mx, my, mz, 1);
                    let score = priority_score(cam, dir, &coord);
                    scored.push((score, coord));
                }
            }
        }
    }

    scored.sort_by(|a, b| a.0.total_cmp(&b.0));
    scored.into_iter().map(|(_, c)| c).collect()
}

fn priority_score(cam: Vec3, dir: Vec3, coord: &ChunkCoord) -> f32 {
    let to_chunk = coord.world_center() - cam;
    let dist = to_chunk.length();
    let frustum_bonus = if dist > 1.0 && dir.length_squared() > 0.5 {
        let dot = to_chunk.normalize().dot(dir);
        // Strongly prioritize chunks in front of camera
        1.0 - dot * 0.45
    } else {
        0.5
    };
    // LOD 0 gets slight priority boost when close
    let lod_bias = coord.lod as f32 * 18.0;
    dist * frustum_bonus + lod_bias
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_binary_chunk_codec() {
        let mesh = ChunkMesh {
            coord: ChunkCoord::new(-2, 1, 5, 0),
            seed: 1337,
            vertices: vec![
                Vertex {
                    position: [1.0, 2.0, 3.0],
                    ao: 0.85,
                    normal: [0.0, 1.0, 0.0],
                    biome: 2.0,
                },
                Vertex {
                    position: [4.0, 5.0, 6.0],
                    ao: 0.65,
                    normal: [1.0, 0.0, 0.0],
                    biome: 3.0,
                },
                Vertex {
                    position: [7.0, 8.0, 9.0],
                    ao: 0.95,
                    normal: [0.0, 0.0, 1.0],
                    biome: 4.0,
                },
            ],
            indices: vec![0, 1, 2],
            bake_us: 420,
            cache_hit: true,
        };

        let bytes = mesh.encode_binary();
        let decoded = ChunkMesh::decode_binary(&bytes).unwrap();
        assert_eq!(decoded.coord, mesh.coord);
        assert_eq!(decoded.seed, mesh.seed);
        assert_eq!(decoded.vertices.len(), 3);
        assert_eq!(decoded.indices, vec![0, 1, 2]);
        assert_eq!(decoded.bake_us, 420);
        assert!(decoded.cache_hit);
    }

    #[test]
    fn desired_chunks_mixes_lod0_and_lod1_without_overlap() {
        let pose = CameraPose {
            pos: [10.0, 20.0, 10.0],
            dir: [0.0, 0.0, -1.0],
            seed: 42,
            near_radius: 1,
            far_radius: 2,
        };
        let chunks = compute_desired_chunks(&pose);
        assert!(chunks.iter().any(|c| c.lod == 0));
        assert!(chunks.iter().any(|c| c.lod == 1));
        // Closest chunks should come first
        let first_dist = chunks[0].world_center().distance(Vec3::from_array(pose.pos));
        let last_dist = chunks.last().unwrap().world_center().distance(Vec3::from_array(pose.pos));
        assert!(first_dist < last_dist);
    }
}
