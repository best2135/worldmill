// WorldMill GPU Compute Pipeline — 3D Procedural SDF + Seamless Surface Nets
// Runs headless on native Vulkan/DX12/Metal via Rust `wgpu` worker pool.

struct ChunkUniforms {
    origin: vec3<f32>,
    voxel_size: f32,
    seed: u32,
    chunk_cells: u32,      // 24
    cell_grid_dim: u32,    // 25
    density_grid_dim: u32, // 26
    max_vertices: u32,
    max_indices: u32,
    bedrock_y: f32,
    _pad1: u32,
};

struct Vertex {
    position: vec3<f32>,
    ao: f32,
    normal: vec3<f32>,
    biome: f32,
};

struct Counters {
    vertex_count: atomic<u32>,
    index_count: atomic<u32>,
};

@group(0) @binding(0) var<uniform> params: ChunkUniforms;
@group(0) @binding(1) var<storage, read_write> densities: array<f32>;
@group(0) @binding(2) var<storage, read_write> cell_to_vert: array<u32>;
@group(0) @binding(3) var<storage, read_write> vertices: array<Vertex>;
@group(0) @binding(4) var<storage, read_write> indices: array<u32>;
@group(0) @binding(5) var<storage, read_write> counters: Counters;

// -----------------------------------------------------------------------------
// Deterministic 3D Hash & Smooth Value Noise (fast, artifact-free, seedable)
// -----------------------------------------------------------------------------
fn hash31(p: vec3<f32>, seed: f32) -> f32 {
    var q = fract(p * vec3<f32>(0.1031, 0.1030, 0.0973) + vec3<f32>(seed * 0.0173, seed * 0.0317, seed * 0.0531));
    q += dot(q, q.yxz + 33.33);
    return fract((q.x + q.y) * q.z);
}

fn noise3(p: vec3<f32>, seed: f32) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);

    let n000 = hash31(i + vec3<f32>(0.0, 0.0, 0.0), seed);
    let n100 = hash31(i + vec3<f32>(1.0, 0.0, 0.0), seed);
    let n010 = hash31(i + vec3<f32>(0.0, 1.0, 0.0), seed);
    let n110 = hash31(i + vec3<f32>(1.0, 1.0, 0.0), seed);
    let n001 = hash31(i + vec3<f32>(0.0, 0.0, 1.0), seed);
    let n101 = hash31(i + vec3<f32>(1.0, 0.0, 1.0), seed);
    let n011 = hash31(i + vec3<f32>(0.0, 1.0, 1.0), seed);
    let n111 = hash31(i + vec3<f32>(1.0, 1.0, 1.0), seed);

    let nx00 = mix(n000, n100, u.x);
    let nx10 = mix(n010, n110, u.x);
    let nx01 = mix(n001, n101, u.x);
    let nx11 = mix(n011, n111, u.x);
    let nxy0 = mix(nx00, nx10, u.y);
    let nxy1 = mix(nx01, nx11, u.y);
    return mix(nxy0, nxy1, u.z) * 2.0 - 1.0;
}

fn fbm3(p_in: vec3<f32>, seed: f32, octaves: i32) -> f32 {
    var sum = 0.0;
    var amp = 0.5;
    var freq = 1.0;
    for (var i = 0; i < octaves; i = i + 1) {
        sum += amp * noise3(p_in * freq, seed + f32(i) * 19.7);
        freq *= 2.03;
        amp *= 0.5;
    }
    return sum;
}

fn noise2(p: vec2<f32>, seed: f32) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = hash31(vec3<f32>(i, 0.0), seed);
    let b = hash31(vec3<f32>(i + vec2<f32>(1.0, 0.0), 0.0), seed);
    let c = hash31(vec3<f32>(i + vec2<f32>(0.0, 1.0), 0.0), seed);
    let d = hash31(vec3<f32>(i + vec2<f32>(1.0, 1.0), 0.0), seed);
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 2.0 - 1.0;
}

fn smooth_min(a: f32, b: f32, width: f32) -> f32 {
    let overlap = max(width - abs(a - b), 0.0) / width;
    return min(a, b) - 0.25 * width * overlap * overlap;
}

fn smooth_max(a: f32, b: f32, width: f32) -> f32 {
    return -smooth_min(-a, -b, width);
}

fn terrain_height(xz: vec2<f32>, seed: f32) -> f32 {
    let warp = vec2<f32>(
        noise2(xz * 0.005 + vec2<f32>(3.1, 7.8), seed + 3.0),
        noise2(xz * 0.005 + vec2<f32>(8.4, 2.6), seed + 11.0)
    );
    let warped = xz + warp * 54.0;
    let continent = noise2(warped * 0.0028, seed);
    let uplift = smoothstep(-0.30, 0.65, continent);
    let ridge_noise = noise2(warped * vec2<f32>(0.008, 0.0045), seed + 41.0);
    let ridge = 1.0 - sqrt(ridge_noise * ridge_noise + 0.003);
    let foothills = noise2(warped * 0.018, seed + 59.0) * 7.0;
    let uplands = 5.0 + 12.0 * continent + ridge * ridge * (20.0 + 66.0 * uplift) + foothills;

    // A sinuous river centerline cuts a trough with rounded shelves and steep risers.
    let channel_noise = noise2(warped * vec2<f32>(0.0055, 0.0035), seed + 67.0);
    let channel = sqrt(channel_noise * channel_noise + 0.001);
    let incision = 34.0 * (1.0 - smoothstep(0.045, 0.46, channel));
    let cut = uplands - incision;
    let step_height = 6.0;
    let layer = cut / step_height;
    let shelves = step_height * (floor(layer) + smoothstep(0.20, 0.80, fract(layer)));
    let canyon = 1.0 - smoothstep(0.36, 0.69, channel);
    return mix(cut, shelves, canyon);
}

fn add_rock_arch(d: f32, p: vec3<f32>, height: f32, relief: f32, seed: f32) -> f32 {
    let spacing = 144.0;
    let tile = floor(p.xz / spacing + 0.5);
    let tile_seed = vec3<f32>(tile, 3.0);
    let center_offset = vec2<f32>(hash31(tile_seed, seed + 211.0),
                                 hash31(tile_seed, seed + 229.0)) * 24.0 - 12.0;
    let local = p.xz - tile * spacing - center_offset;
    let rise = p.y - height;
    // The finite arch fits inside its tile, including the blend into the terrain.
    if (dot(local, local) > 48.0 * 48.0 || rise > 48.0 || rise < -12.0) {
        return d;
    }

    let angle = hash31(tile_seed, seed + 257.0) * 6.2831853;
    let along = vec2<f32>(cos(angle), sin(angle));
    let span = dot(local, along);
    let depth = dot(local, vec2<f32>(-along.y, along.x));
    let width = mix(17.0, 26.0, hash31(tile_seed, seed + 271.0));
    let arch_height = mix(16.0, 24.0, hash31(tile_seed, seed + 283.0));

    // The upper half ellipse forms the crown. Its legs extend below the terrain.
    let crown = max(rise - 4.0, 0.0);
    let ring = length(vec2<f32>(span / width, crown / arch_height));
    let shell = abs(ring - 1.0) * min(width, arch_height) - 3.5;
    let rock = smooth_max(shell, abs(depth) - 6.0, 3.0) + relief * 0.6;
    return smooth_min(d, rock, 3.5);
}

// d < 0 is solid and d >= 0 is air.
struct SdfSample {
    dist: f32,
    cave_factor: f32,
};

fn sample_terrain(p: vec3<f32>, height: f32, seed: f32) -> SdfSample {
    let overhang = fbm3(p * vec3<f32>(0.022, 0.037, 0.022), seed + 77.0, 2) * 10.0;
    let detail = noise3(p * 0.086, seed + 131.0) * 0.9;
    let relief = overhang + detail;
    var d = p.y - height + relief;
    d = add_rock_arch(d, p, height, relief, seed);

    // Intersect two warped noise isosurfaces to form linked tunnels. The wider
    // sections reach the canyon walls and open up into cavern entrances.
    var cave_factor = 0.0;
    if (d < 13.0) {
        let cave_p = p * vec3<f32>(0.022, 0.031, 0.022);
        let c1 = fbm3(cave_p, seed + 307.0, 2);
        let c2 = fbm3(cave_p + vec3<f32>(17.3, -9.1, 23.7), seed + 409.0, 2);
        let tunnel = sqrt(c1 * c1 + c2 * c2 + 0.0004);
        let chamber = smoothstep(-0.2, 0.65, noise3(cave_p * 0.42, seed + 463.0));
        let radius = mix(0.115, 0.245, chamber);
        let carve = (radius - tunnel) * 42.0;
        let carved = smooth_max(d, carve, 2.5);
        cave_factor = smoothstep(0.0, 4.0, carved - d);
        d = carved;
    }

    // A smooth union with the floor guarantees solid bedrock below y = -40.
    return SdfSample(smooth_min(d, p.y - params.bedrock_y, 1.5), cave_factor);
}

fn eval_world_sdf(p: vec3<f32>) -> SdfSample {
    let seed = f32(params.seed % 10000u);
    return sample_terrain(p, terrain_height(p.xz, seed), seed);
}

fn density_idx(x: u32, y: u32, z: u32) -> u32 {
    let dim = params.density_grid_dim;
    return x + y * dim + z * dim * dim;
}

fn cell_idx(x: u32, y: u32, z: u32) -> u32 {
    let dim = params.cell_grid_dim;
    return x + y * dim + z * dim * dim;
}

// -----------------------------------------------------------------------------
// Pass 1: Evaluate 3D SDF density grid (26 x 26 x 26)
// -----------------------------------------------------------------------------
var<workgroup> column_heights: array<f32, 16>;

@compute @workgroup_size(4, 4, 4)
fn eval_density(@builtin(global_invocation_id) gid: vec3<u32>,
                @builtin(local_invocation_id) lid: vec3<u32>) {
    let dim = params.density_grid_dim;
    let world_p = params.origin + vec3<f32>(gid) * params.voxel_size;
    let column = lid.x + 4u * lid.z;
    let seed = f32(params.seed % 10000u);

    if (lid.y == 0u && gid.x < dim && gid.z < dim) {
        column_heights[column] = terrain_height(world_p.xz, seed);
    }
    workgroupBarrier();

    if (gid.x >= dim || gid.y >= dim || gid.z >= dim) {
        return;
    }
    densities[density_idx(gid.x, gid.y, gid.z)] =
        sample_terrain(world_p, column_heights[column], seed).dist;
}

// -----------------------------------------------------------------------------
// Pass 2: Surface Nets Vertex Placement + Gradient Normals + SDF AO + Biomes
// -----------------------------------------------------------------------------
fn compute_normal(p: vec3<f32>, eps: f32, height: f32, seed: f32) -> vec3<f32> {
    let dx = eval_world_sdf(p + vec3<f32>(eps, 0.0, 0.0)).dist
           - eval_world_sdf(p - vec3<f32>(eps, 0.0, 0.0)).dist;
    let dy = sample_terrain(p + vec3<f32>(0.0, eps, 0.0), height, seed).dist
           - sample_terrain(p - vec3<f32>(0.0, eps, 0.0), height, seed).dist;
    let dz = eval_world_sdf(p + vec3<f32>(0.0, 0.0, eps)).dist
           - eval_world_sdf(p - vec3<f32>(0.0, 0.0, eps)).dist;
    let g = vec3<f32>(dx, dy, dz);
    let len = length(g);
    if (len < 1e-5) {
        return vec3<f32>(0.0, 1.0, 0.0);
    }
    return g / len;
}

fn compute_ao(p: vec3<f32>, n: vec3<f32>, step: f32) -> f32 {
    var occ = 0.0;
    var weight = 0.55;
    for (var i = 1; i <= 3; i = i + 1) {
        let dist_t = step * f32(i) * 1.45;
        let d = eval_world_sdf(p + n * dist_t).dist;
        occ += weight * max(0.0, (dist_t - d) / dist_t);
        weight *= 0.55;
    }
    return clamp(1.0 - occ * 0.85, 0.18, 1.0);
}

fn classify_biome(p: vec3<f32>, n: vec3<f32>, cave_factor: f32) -> f32 {
    let seed = f32(params.seed % 10000u);
    // Deep cavern with occasional glowing crystal veins
    if (cave_factor > 0.35 || p.y < -14.0) {
        let crystal = noise3(p * 0.14, seed + 919.0);
        if (crystal > 0.52 && p.y < -4.0) {
            return 5.0; // Bioluminescent crystal vein
        }
        return 0.0; // Cavern basalt
    }
    // Steep cliffs or undersides of overhangs
    if (n.y < 0.58) {
        return 3.0; // Striated cliff rock
    }
    // High alpine peaks & sky arches
    let snow_line = 26.0 + noise3(p * 0.03, seed + 521.0) * 6.0;
    if (p.y > snow_line) {
        return 4.0; // Alpine snow
    }
    // Low valley floor / warm sandstone
    if (p.y < -2.5) {
        return 1.0; // Warm sandstone / shore
    }
    return 2.0; // Lush highland meadow
}

@compute @workgroup_size(4, 4, 4)
fn build_vertices(@builtin(global_invocation_id) gid: vec3<u32>) {
    let dim = params.cell_grid_dim;
    if (gid.x >= dim || gid.y >= dim || gid.z >= dim) {
        return;
    }

    let c_idx = cell_idx(gid.x, gid.y, gid.z);

    // Fetch 8 corner densities
    var d: array<f32, 8>;
    var mask: u32 = 0u;
    for (var i = 0u; i < 8u; i = i + 1u) {
        let ox = i & 1u;
        let oy = (i >> 1u) & 1u;
        let oz = (i >> 2u) & 1u;
        let val = densities[density_idx(gid.x + ox, gid.y + oy, gid.z + oz)];
        d[i] = val;
        if (val < 0.0) {
            mask |= (1u << i);
        }
    }

    // No sign change => cell does not contain surface
    if (mask == 0u || mask == 255u) {
        cell_to_vert[c_idx] = 0xFFFFFFFFu;
        return;
    }

    // 12 edges of unit cube (pairs of corner indices)
    var edge_a = array<u32, 12>(0u, 2u, 4u, 6u, 0u, 1u, 4u, 5u, 0u, 1u, 2u, 3u);
    var edge_b = array<u32, 12>(1u, 3u, 5u, 7u, 2u, 3u, 6u, 7u, 4u, 5u, 6u, 7u);

    var avg = vec3<f32>(0.0);
    var hits = 0.0;

    for (var e = 0u; e < 12u; e = e + 1u) {
        let a = edge_a[e];
        let b = edge_b[e];
        let da = d[a];
        let db = d[b];
        if ((da < 0.0) != (db < 0.0)) {
            let denom = da - db;
            let t = clamp(select(0.5, da / denom, abs(denom) > 1e-6), 0.0, 1.0);
            let pa = vec3<f32>(f32(a & 1u), f32((a >> 1u) & 1u), f32((a >> 2u) & 1u));
            let pb = vec3<f32>(f32(b & 1u), f32((b >> 1u) & 1u), f32((b >> 2u) & 1u));
            avg += mix(pa, pb, t);
            hits += 1.0;
        }
    }

    let local_pos = select(vec3<f32>(0.5), avg / hits, hits > 0.0);
    let cell_origin = params.origin + vec3<f32>(gid) * params.voxel_size;
    let world_pos = cell_origin + local_pos * params.voxel_size;

    let seed = f32(params.seed % 10000u);
    let height = terrain_height(world_pos.xz, seed);
    let normal = compute_normal(world_pos, params.voxel_size * 0.45, height, seed);
    let ao = compute_ao(world_pos, normal, params.voxel_size);
    let sample = sample_terrain(world_pos, height, seed);
    let biome = classify_biome(world_pos, normal, sample.cave_factor);

    let vid = atomicAdd(&counters.vertex_count, 1u);
    if (vid < params.max_vertices) {
        vertices[vid] = Vertex(world_pos, ao, normal, biome);
        cell_to_vert[c_idx] = vid;
    } else {
        cell_to_vert[c_idx] = 0xFFFFFFFFu;
    }
}

// -----------------------------------------------------------------------------
// Pass 3: Emit Seamless Outward-Wound Quads (2 Triangles Each)
// -----------------------------------------------------------------------------
fn emit_quad(v0: u32, v1: u32, v2: u32, v3: u32, flip: bool) {
    if (v0 == 0xFFFFFFFFu || v1 == 0xFFFFFFFFu || v2 == 0xFFFFFFFFu || v3 == 0xFFFFFFFFu) {
        return;
    }
    let base = atomicAdd(&counters.index_count, 6u);
    if (base + 6u > params.max_indices) {
        return;
    }
    if (!flip) {
        indices[base + 0u] = v0;
        indices[base + 1u] = v1;
        indices[base + 2u] = v2;
        indices[base + 3u] = v0;
        indices[base + 4u] = v2;
        indices[base + 5u] = v3;
    } else {
        indices[base + 0u] = v0;
        indices[base + 1u] = v2;
        indices[base + 2u] = v1;
        indices[base + 3u] = v0;
        indices[base + 4u] = v3;
        indices[base + 5u] = v2;
    }
}

@compute @workgroup_size(4, 4, 4)
fn emit_indices(@builtin(global_invocation_id) gid: vec3<u32>) {
    let n = params.chunk_cells; // 24
    let x = gid.x;
    let y = gid.y;
    let z = gid.z;
    if (x > n || y > n || z > n) {
        return;
    }

    // This cell supplies one vertex to every quad owned by this invocation.
    let v2 = cell_to_vert[cell_idx(x, y, z)];
    if (v2 == 0xFFFFFFFFu) {
        return;
    }
    let d0 = densities[density_idx(x, y, z)];

    // X-edge: (x, y, z) -> (x+1, y, z), x in 0..n, y in 1..=n, z in 1..=n
    if (x < n && y >= 1u && z >= 1u) {
        let d1 = densities[density_idx(x + 1u, y, z)];
        if ((d0 < 0.0) != (d1 < 0.0)) {
            let v0 = cell_to_vert[cell_idx(x, y - 1u, z - 1u)];
            let v1 = cell_to_vert[cell_idx(x, y,      z - 1u)];
            let v3 = cell_to_vert[cell_idx(x, y - 1u, z     )];
            // Solid at d0, Air at d1 => outward normal +X => flip = false
            emit_quad(v0, v1, v2, v3, d0 >= 0.0);
        }
    }

    // Y-edge: (x, y, z) -> (x, y+1, z), x in 1..=n, y in 0..n, z in 1..=n
    if (x >= 1u && y < n && z >= 1u) {
        let d1 = densities[density_idx(x, y + 1u, z)];
        if ((d0 < 0.0) != (d1 < 0.0)) {
            let v0 = cell_to_vert[cell_idx(x - 1u, y, z - 1u)];
            let v1 = cell_to_vert[cell_idx(x - 1u, y, z     )];
            let v3 = cell_to_vert[cell_idx(x,      y, z - 1u)];
            // Solid at d0, Air at d1 => outward normal +Y => flip = false
            emit_quad(v0, v1, v2, v3, d0 >= 0.0);
        }
    }

    // Z-edge: (x, y, z) -> (x, y, z+1), x in 1..=n, y in 1..=n, z in 0..n
    if (x >= 1u && y >= 1u && z < n) {
        let d1 = densities[density_idx(x, y, z + 1u)];
        if ((d0 < 0.0) != (d1 < 0.0)) {
            let v0 = cell_to_vert[cell_idx(x - 1u, y - 1u, z)];
            let v1 = cell_to_vert[cell_idx(x,      y - 1u, z)];
            let v3 = cell_to_vert[cell_idx(x - 1u, y,      z)];
            // Solid at d0, Air at d1 => outward normal +Z => flip = false
            emit_quad(v0, v1, v2, v3, d0 >= 0.0);
        }
    }
}
