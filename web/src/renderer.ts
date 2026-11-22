import { invertMat4, lookAt, multiplyMat4, perspectiveZO, type Vec3 } from "./math";
import { chunkKey, type ChunkCoord, type DecodedChunk } from "./protocol";

interface GpuChunkEntry {
  vertexBuffer: GPUBuffer | null;
  indexBuffer: GPUBuffer | null;
  indexCount: number;
  triangleCount: number;
  lod: number;
  uniformBuffer: GPUBuffer;
  bindGroup: GPUBindGroup;
}

const SKY_WGSL = /* wgsl */ `
struct FrameUniforms {
    view_proj: mat4x4<f32>,
    inv_view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
    sun_dir: vec4<f32>,
    params: vec4<f32>, // x = time, y = lod_tint
};

@group(0) @binding(0) var<uniform> frame: FrameUniforms;

struct SkyOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vs_sky(@builtin(vertex_index) vid: u32) -> SkyOut {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 3.0, -1.0),
        vec2<f32>(-1.0,  3.0),
    );
    let p = positions[vid];
    return SkyOut(vec4<f32>(p, 0.9999, 1.0), p);
}

fn sky_hash21(p: vec2<f32>) -> f32 {
    var p3 = fract(vec3<f32>(p.xyx) * 0.1031);
    p3 += vec3<f32>(dot(p3, p3.yzx + vec3<f32>(33.33)));
    return fract((p3.x + p3.y) * p3.z);
}

fn sky_vnoise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    let a = sky_hash21(i);
    let b = sky_hash21(i + vec2<f32>(1.0, 0.0));
    let c = sky_hash21(i + vec2<f32>(0.0, 1.0));
    let d = sky_hash21(i + vec2<f32>(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

fn sky_fbm5(p_in: vec2<f32>) -> f32 {
    var p = p_in;
    var v = 0.0;
    var a = 0.5;
    let rot = mat2x2<f32>(1.624, 1.218, -1.218, 1.624);
    for (var i: i32 = 0; i < 5; i = i + 1) {
        v += a * sky_vnoise(p);
        p = rot * p + vec2<f32>(13.7, 29.3);
        a *= 0.5;
    }
    return v;
}

fn sky_fbm3(p_in: vec2<f32>) -> f32 {
    var p = p_in;
    var v = 0.0;
    var a = 0.5;
    let rot = mat2x2<f32>(1.624, 1.218, -1.218, 1.624);
    for (var i: i32 = 0; i < 3; i = i + 1) {
        v += a * sky_vnoise(p);
        p = rot * p + vec2<f32>(7.1, 19.4);
        a *= 0.5;
    }
    return v;
}

fn cloud_density(uv: vec2<f32>) -> f32 {
    let warp = vec2<f32>(
        sky_fbm3(uv * 0.45 + vec2<f32>(4.2, 1.7)),
        sky_fbm3(uv * 0.45 + vec2<f32>(9.1, 6.3))
    );
    let base = sky_fbm5(uv + (warp - vec2<f32>(0.5)) * 0.65);
    let billow = 1.0 - abs(sky_fbm3(uv * 2.1 + vec2<f32>(3.4)) * 2.0 - 1.0);
    let raw = mix(base, base * 0.72 + billow * 0.34, 0.38);
    return smoothstep(0.47, 0.78, raw);
}

@fragment
fn fs_sky(in: SkyOut) -> @location(0) vec4<f32> {
    let clip = vec4<f32>(in.ndc, 1.0, 1.0);
    let world_h = frame.inv_view_proj * clip;
    let dir = normalize(world_h.xyz / world_h.w - frame.camera_pos.xyz);

    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
    let sun_vis = smoothstep(-0.08, 0.14, sun.y);

    // Match terrain & water linear horizon fog color for a seamless horizon
    let horizon_base = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.32, 0.12), sunset_t);
    let zenith = mix(vec3<f32>(0.075, 0.21, 0.56), vec3<f32>(0.085, 0.105, 0.29), sunset_t);
    let ground = mix(vec3<f32>(0.10, 0.13, 0.19), vec3<f32>(0.18, 0.09, 0.09), sunset_t);

    let sun_dot = max(dot(dir, sun), 0.0);
    let mie_horizon = pow(sun_dot, 4.0) * exp(-abs(dir.y) * 4.5);
    let horizon = horizon_base + vec3<f32>(1.10, 0.48, 0.16) * mie_horizon * (0.28 + 0.65 * sunset_t) * sun_vis;

    var sky = select(
        mix(ground, horizon, clamp(dir.y * 3.8 + 1.0, 0.0, 1.0)),
        mix(horizon, zenith, pow(max(dir.y, 0.0), 0.52)),
        dir.y >= 0.0,
    );

    // Atmospheric Rayleigh/Mie sun glow & crisp solar disc
    let glow_col = mix(vec3<f32>(1.15, 0.90, 0.60), vec3<f32>(1.55, 0.52, 0.16), sunset_t);
    let disc_col = mix(vec3<f32>(2.80, 2.55, 2.15), vec3<f32>(2.95, 1.65, 0.65), sunset_t);
    sky += glow_col * pow(sun_dot, 8.0) * (0.24 + 0.36 * sunset_t) * sun_vis;
    sky += glow_col * pow(sun_dot, 48.0) * 0.55 * sun_vis;
    sky += disc_col * smoothstep(0.9988, 0.9997, sun_dot) * 2.4 * sun_vis;

    // Volumetric-style layered clouds on a curved atmospheric dome
    if (dir.y > 0.005) {
        let t_time = frame.params.x;
        let R_earth = 2400.0;
        let H_cloud = 24.0;
        let ray_dist = -R_earth * dir.y + sqrt(R_earth * R_earth * dir.y * dir.y + 2.0 * R_earth * H_cloud + H_cloud * H_cloud);
        let dome_pos = dir * ray_dist;
        let wind = vec2<f32>(t_time * 0.018, t_time * 0.007);
        let cloud_uv = dome_pos.xz * 0.011 + wind;

        // High-altitude wispy cirrus layer
        let cirrus_uv = dome_pos.xz * 0.0065 * vec2<f32>(1.0, 2.2) + wind * 1.4;
        let cirrus = smoothstep(0.54, 0.84, sky_fbm5(cirrus_uv)) * 0.36;
        let cirrus_col = mix(vec3<f32>(0.92, 0.96, 1.02), vec3<f32>(1.32, 0.72, 0.45), sunset_t);

        // Primary cumulus layer with directional light-marching toward sun
        let dens = cloud_density(cloud_uv);
        let sun_horiz = normalize(vec3<f32>(sun.x, 0.0, sun.z) + vec3<f32>(1e-4, 0.0, 0.0)).xz;
        let s1 = cloud_density(cloud_uv + sun_horiz * 0.09);
        let s2 = cloud_density(cloud_uv + sun_horiz * 0.22);
        let optical_depth = (s1 * 0.65 + s2 * 0.35);
        let light_trans = exp(-optical_depth * 2.35);

        let cloud_shadow = mix(vec3<f32>(0.28, 0.36, 0.50), vec3<f32>(0.34, 0.24, 0.38), sunset_t);
        let cloud_lit = mix(vec3<f32>(1.28, 1.24, 1.18), vec3<f32>(1.65, 0.78, 0.40), sunset_t);
        let hg_forward = pow(sun_dot, 7.0) * (1.0 - dens * 0.65) * 0.85;
        var cloud_col = mix(cloud_shadow, cloud_lit, clamp(light_trans * 1.15, 0.0, 1.0));
        cloud_col += glow_col * hg_forward * sun_vis;

        let horizon_fade = smoothstep(0.01, 0.16, dir.y);
        sky = mix(sky, cirrus_col, cirrus * horizon_fade * 0.75);
        sky = mix(sky, cloud_col, dens * horizon_fade * 0.92);
    }

    // ACES filmic tone mapping + gamma matching terrain & water
    sky = (sky * (2.51 * sky + vec3<f32>(0.03))) / (sky * (2.43 * sky + vec3<f32>(0.59)) + vec3<f32>(0.14));
    sky = pow(clamp(sky, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1.0 / 2.2));
    return vec4<f32>(sky, 1.0);
}
`;

const TERRAIN_WGSL = /* wgsl */ `
struct FrameUniforms {
    view_proj: mat4x4<f32>,
    inv_view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
    sun_dir: vec4<f32>,
    params: vec4<f32>, // x = time, y = lod_tint
};

struct ChunkUniforms {
    lod_info: vec4<f32>, // x = lod
};

@group(0) @binding(0) var<uniform> frame: FrameUniforms;
@group(1) @binding(0) var<uniform> chunk: ChunkUniforms;

struct VertIn {
    @location(0) position: vec3<f32>,
    @location(1) ao: f32,
    @location(2) normal: vec3<f32>,
    @location(3) biome: f32,
};

struct VertOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0) world_pos: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) ao: f32,
    @location(3) biome: f32,
};

@vertex
fn vs_main(v: VertIn) -> VertOut {
    var out: VertOut;
    out.clip_pos = frame.view_proj * vec4<f32>(v.position, 1.0);
    out.world_pos = v.position;
    out.normal = v.normal;
    out.ao = v.ao;
    out.biome = v.biome;
    return out;
}

const PI: f32 = 3.14159265;
const WATER_LEVEL: f32 = -6.0;

fn hash21(p: vec2<f32>) -> f32 {
    var p3 = fract(vec3<f32>(p.xyx) * 0.1031);
    p3 += vec3<f32>(dot(p3, p3.yzx + vec3<f32>(33.33)));
    return fract((p3.x + p3.y) * p3.z);
}

fn hash22(p: vec2<f32>) -> vec2<f32> {
    var p3 = fract(vec3<f32>(p.xyx) * vec3<f32>(0.1031, 0.1030, 0.0973));
    p3 += vec3<f32>(dot(p3, p3.yzx + vec3<f32>(33.33)));
    return fract((p3.xx + p3.yz) * p3.zy);
}

fn hash33(p_in: vec3<f32>) -> vec3<f32> {
    var p = fract(p_in * vec3<f32>(0.1031, 0.1030, 0.0973));
    p += vec3<f32>(dot(p, p.yxz + vec3<f32>(33.33)));
    return fract((p.xxy + p.yxx) * p.zyx);
}

// C2 quintic value noise with exact analytical 2D gradient: (val, dval/dx, dval/dy)
fn vnoise_d(p: vec2<f32>) -> vec3<f32> {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    let du = 30.0 * f * f * (f * (f - 2.0) + 1.0);

    let a = hash21(i);
    let b = hash21(i + vec2<f32>(1.0, 0.0));
    let c = hash21(i + vec2<f32>(0.0, 1.0));
    let d = hash21(i + vec2<f32>(1.0, 1.0));

    let k1 = b - a;
    let k2 = c - a;
    let k3 = a - b - c + d;

    let val = a + k1 * u.x + k2 * u.y + k3 * u.x * u.y;
    let grad = vec2<f32>(k1 + k3 * u.y, k2 + k3 * u.x) * du;
    return vec3<f32>(val, grad.x, grad.y);
}

// 4-octave rotated FBM returning (val in ~[0,1], d/dx, d/dy)
fn fbm4_d(p_in: vec2<f32>) -> vec3<f32> {
    var p = p_in;
    var val = 0.0;
    var grad = vec2<f32>(0.0, 0.0);
    var amp = 0.5;
    let step_m = mat2x2<f32>(1.624, 1.218, -1.218, 1.624);
    let step_t = mat2x2<f32>(1.624, -1.218, 1.218, 1.624);
    var mt = mat2x2<f32>(1.0, 0.0, 0.0, 1.0);

    for (var i: i32 = 0; i < 4; i = i + 1) {
        let n = vnoise_d(p);
        val += amp * n.x;
        grad += amp * (mt * n.yz);
        p = step_m * p + vec2<f32>(17.1, 31.7);
        mt = step_t * mt;
        amp *= 0.5;
    }
    return vec3<f32>(val, grad.x, grad.y);
}

// 2D cellular Voronoi returning (f1, edge_dist = f2 - f1, cell_id)
fn voronoi2d(p: vec2<f32>) -> vec3<f32> {
    let i = floor(p);
    let f = fract(p);
    var d1 = 8.0;
    var d2 = 8.0;
    var id = 0.0;
    for (var y: i32 = -1; y <= 1; y = y + 1) {
        for (var x: i32 = -1; x <= 1; x = x + 1) {
            let g = vec2<f32>(f32(x), f32(y));
            let o = hash22(i + g);
            let r = g + o - f;
            let d = dot(r, r);
            if (d < d1) {
                d2 = d1;
                d1 = d;
                id = hash21(i + g);
            } else if (d < d2) {
                d2 = d;
            }
        }
    }
    let sd1 = sqrt(d1);
    let sd2 = sqrt(d2);
    return vec3<f32>(sd1, sd2 - sd1, id);
}

fn triplanar_weights(n: vec3<f32>) -> vec3<f32> {
    let w = pow(abs(n), vec3<f32>(4.0));
    return w / max(w.x + w.y + w.z, 1e-4);
}

// Returns vec4(value, grad_world.x, grad_world.y, grad_world.z)
fn triplanar_fbm4_d(wp: vec3<f32>, w: vec3<f32>, scale: f32) -> vec4<f32> {
    let pyz = fbm4_d(wp.zy * scale + vec2<f32>(3.1, 7.7));
    let pxz = fbm4_d(wp.xz * scale + vec2<f32>(19.3, 11.9));
    let pxy = fbm4_d(wp.xy * scale + vec2<f32>(41.7, 23.5));

    let val = pyz.x * w.x + pxz.x * w.y + pxy.x * w.z;
    let g_yz = vec3<f32>(0.0, pyz.z, pyz.y) * scale;
    let g_xz = vec3<f32>(pxz.y, 0.0, pxz.z) * scale;
    let g_xy = vec3<f32>(pxy.y, pxy.z, 0.0) * scale;
    let grad = g_yz * w.x + g_xz * w.y + g_xy * w.z;
    return vec4<f32>(val, grad.x, grad.y, grad.z);
}

fn triplanar_voronoi(wp: vec3<f32>, w: vec3<f32>, scale: f32) -> vec3<f32> {
    var acc = vec3<f32>(0.0);
    if (w.x > 0.02) { acc += voronoi2d(wp.zy * scale + vec2<f32>(5.2, 8.4)) * w.x; }
    if (w.y > 0.02) { acc += voronoi2d(wp.xz * scale + vec2<f32>(14.7, 2.9)) * w.y; }
    if (w.z > 0.02) { acc += voronoi2d(wp.xy * scale + vec2<f32>(29.1, 17.6)) * w.z; }
    return acc;
}

struct MatSample {
    albedo: vec3<f32>,
    roughness: f32,
    metallic: f32,
    f0: f32,
    cavity: f32,
    emissive: vec3<f32>,
    bump_grad: vec3<f32>,
    snow_mask: f32,
};

fn eval_granite(
    wp: vec3<f32>,
    n: vec3<f32>,
    w: vec3<f32>,
    macro_n: vec4<f32>,
    micro_n: vec4<f32>,
    detail_n: vec4<f32>,
    vor: vec3<f32>,
) -> MatSample {
    var m: MatSample;
    let strata_coord = wp.y * 0.65 + macro_n.x * 2.2 + micro_n.x * 0.38;
    let strata = 0.5 + 0.5 * sin(strata_coord * 3.1 + sin(strata_coord * 1.4) * 1.2);
    let strata_grad = vec3<f32>(0.0, cos(strata_coord * 3.1) * 0.65, 0.0);

    let crack = 1.0 - smoothstep(0.015, 0.14, vor.y);
    let lichen = smoothstep(0.66, 0.88, n.y) * smoothstep(0.48, 0.74, macro_n.x);
    let scree = smoothstep(0.54, 0.84, n.y) * smoothstep(0.42, 0.68, micro_n.x);

    let cool_rock = vec3<f32>(0.24, 0.25, 0.29);
    let mid_rock = vec3<f32>(0.41, 0.40, 0.39);
    let warm_vein = vec3<f32>(0.46, 0.37, 0.30);
    let lichen_col = vec3<f32>(0.22, 0.31, 0.18);

    var col = mix(cool_rock, mid_rock, strata * 0.72 + macro_n.x * 0.28);
    col = mix(col, warm_vein, smoothstep(0.52, 0.80, micro_n.x) * 0.38);
    col *= 0.78 + 0.38 * detail_n.x;
    col = mix(col, col * 0.34, crack * 0.85);
    col = mix(col, lichen_col * (0.8 + 0.35 * detail_n.x), lichen * 0.72);
    col = mix(col, vec3<f32>(0.45, 0.44, 0.42), scree * 0.25);

    m.albedo = col;
    m.roughness = mix(0.64, 0.88, max(scree, lichen) * 0.75 + crack * 0.25);
    m.metallic = 0.0;
    m.f0 = 0.045;
    m.cavity = mix(1.0, 0.35, crack) * mix(0.82, 1.05, micro_n.x);
    m.emissive = vec3<f32>(0.0);
    m.bump_grad = macro_n.yzw * 0.55 + micro_n.yzw * 0.95 + detail_n.yzw * 0.45 + strata_grad * 0.28;
    m.snow_mask = 0.0;
    return m;
}

fn eval_material(biome: f32, wp: vec3<f32>, n: vec3<f32>, dist: f32) -> MatSample {
    let w = triplanar_weights(n);
    let macro_n = triplanar_fbm4_d(wp, w, 0.13);
    let micro_n = triplanar_fbm4_d(wp, w, 0.78);
    let detail_fade = 1.0 - smoothstep(12.0, 140.0, dist);
    var detail_n = vec4<f32>(0.5, 0.0, 0.0, 0.0);
    if (detail_fade > 0.01) {
        detail_n = triplanar_fbm4_d(wp, w, 2.75);
    }

    let id = i32(round(biome));
    var mat: MatSample;

    if (id == 0) {
        // Biome 0: Cavern Basalt — fractured dark volcanic rock + damp sheen
        let vor = triplanar_voronoi(wp, w, 0.65);
        let crack = 1.0 - smoothstep(0.02, 0.16, vor.y);
        let damp = smoothstep(0.50, 0.72, macro_n.x);

        let dark_basalt = vec3<f32>(0.075, 0.085, 0.115);
        let mineral_basalt = vec3<f32>(0.16, 0.19, 0.24);
        var col = mix(dark_basalt, mineral_basalt, micro_n.x * 0.65 + detail_n.x * 0.35);
        col = mix(col, col * 0.30, crack * 0.9);
        col *= mix(1.0, 0.62, damp);

        mat.albedo = col;
        mat.roughness = mix(0.80, 0.18, damp) + crack * 0.12;
        mat.metallic = 0.04;
        mat.f0 = 0.055;
        mat.cavity = mix(1.0, 0.28, crack) * (0.78 + 0.28 * micro_n.x);
        mat.emissive = vec3<f32>(0.0);
        mat.bump_grad = macro_n.yzw * 0.6 + micro_n.yzw * 1.15 + detail_n.yzw * 0.55 * detail_fade;
        mat.snow_mask = 0.0;
        return mat;
    }

    if (id == 1) {
        // Biome 1: Warm Sandstone / Shore — sedimentary bands + wet darkened shoreline near y = -6.0
        let strata_coord = wp.y * 0.92 + macro_n.x * 2.1 + micro_n.x * 0.35;
        let band_a = 0.5 + 0.5 * sin(strata_coord * 2.7);
        let band_b = 0.5 + 0.5 * sin(strata_coord * 6.4 + 1.3);

        let sand_buff = vec3<f32>(0.76, 0.62, 0.42);
        let terracotta = vec3<f32>(0.58, 0.33, 0.21);
        let pale_ochre = vec3<f32>(0.85, 0.74, 0.54);

        var col = mix(terracotta, sand_buff, band_a);
        col = mix(col, pale_ochre, band_b * 0.45 * macro_n.x);
        col *= 0.84 + 0.30 * detail_n.x;

        // Wet darkened shoreline band near WATER_LEVEL (-6.0)
        let shore_height = wp.y + (macro_n.x - 0.5) * 0.55;
        let wet_shore = 1.0 - smoothstep(WATER_LEVEL - 0.15, WATER_LEVEL + 1.55, shore_height);
        col *= mix(1.0, 0.45, wet_shore);

        mat.albedo = col;
        mat.roughness = mix(0.86, 0.13, wet_shore);
        mat.metallic = 0.0;
        mat.f0 = 0.04;
        mat.cavity = 0.82 + 0.22 * band_a;
        mat.emissive = vec3<f32>(0.0);
        let strata_grad = vec3<f32>(0.0, cos(strata_coord * 2.7) * 0.25, 0.0);
        let bump_scale = mix(1.0, 0.45, wet_shore);
        mat.bump_grad = (macro_n.yzw * 0.4 + micro_n.yzw * 0.85 + detail_n.yzw * 0.48 * detail_fade + strata_grad) * bump_scale;
        mat.snow_mask = 0.0;
        return mat;
    }

    if (id == 2) {
        // Biome 2: Alpine Meadow — multi-tone grass/clover + smooth slope blend into granite outcrops
        let emerald = vec3<f32>(0.11, 0.36, 0.12);
        let sunlit_moss = vec3<f32>(0.26, 0.46, 0.14);
        let warm_earth = vec3<f32>(0.20, 0.21, 0.11);

        var grass_col = mix(emerald, sunlit_moss, smoothstep(0.32, 0.72, macro_n.x));
        grass_col = mix(grass_col, warm_earth, smoothstep(0.62, 0.86, micro_n.x) * 0.35);
        grass_col *= 0.80 + 0.42 * detail_n.x;

        let rock_blend = 1.0 - smoothstep(0.55, 0.79, n.y + (macro_n.x - 0.5) * 0.10);
        if (rock_blend > 0.01) {
            let vor = triplanar_voronoi(wp, w, 0.55);
            let rock = eval_granite(wp, n, w, macro_n, micro_n, detail_n, vor);
            mat.albedo = mix(grass_col, rock.albedo, rock_blend);
            mat.roughness = mix(0.88, rock.roughness, rock_blend);
            mat.metallic = 0.0;
            mat.f0 = mix(0.035, rock.f0, rock_blend);
            mat.cavity = mix(0.85 + 0.20 * micro_n.x, rock.cavity, rock_blend);
            mat.emissive = vec3<f32>(0.0);
            let grass_grad = micro_n.yzw * 0.65 + detail_n.yzw * 0.60 * detail_fade;
            mat.bump_grad = mix(grass_grad, rock.bump_grad, rock_blend);
            mat.snow_mask = 0.0;
            return mat;
        }

        mat.albedo = grass_col;
        mat.roughness = 0.88;
        mat.metallic = 0.0;
        mat.f0 = 0.035;
        mat.cavity = 0.85 + 0.20 * micro_n.x;
        mat.emissive = vec3<f32>(0.0);
        mat.bump_grad = micro_n.yzw * 0.65 + detail_n.yzw * 0.60 * detail_fade;
        mat.snow_mask = 0.0;
        return mat;
    }

    if (id == 3) {
        // Biome 3: Cliff Granite
        let vor = triplanar_voronoi(wp, w, 0.55);
        return eval_granite(wp, n, w, macro_n, micro_n, detail_n, vor);
    }

    if (id == 4) {
        // Biome 4: Alpine Snow — wind-sculpted drifts + icy subsurface + sun sparkle glints
        let wind_wp = vec3<f32>(wp.x * 0.55 + wp.z * 0.35, wp.y * 1.1, wp.z * 0.85 - wp.x * 0.45);
        let drift_n = triplanar_fbm4_d(wind_wp, w, 0.42);
        let sastrugi = smoothstep(0.32, 0.72, drift_n.x);

        let snow_trough = vec3<f32>(0.76, 0.85, 0.95);
        let snow_crest = vec3<f32>(0.95, 0.97, 1.00);
        var col = mix(snow_trough, snow_crest, sastrugi);
        col *= 0.95 + 0.08 * detail_n.x;

        mat.albedo = col;
        mat.roughness = mix(0.46, 0.28, sastrugi);
        mat.metallic = 0.0;
        mat.f0 = 0.042;
        mat.cavity = 0.88 + 0.14 * sastrugi;
        mat.emissive = vec3<f32>(0.0);
        mat.bump_grad = drift_n.yzw * 0.55 + detail_n.yzw * 0.22 * detail_fade;
        mat.snow_mask = 1.0;
        return mat;
    }

    // Biome 5: Bioluminescent Crystal — faceted cyan/violet veins + pulsing emissive rim
    let vor = triplanar_voronoi(wp, w, 0.85);
    let vein = 1.0 - smoothstep(0.01, 0.18, vor.y);
    let facet = vor.z;
    let pulse = 0.76 + 0.24 * sin(frame.params.x * 2.2 + facet * 6.283 + wp.y * 0.35);

    let cyan_col = vec3<f32>(0.08, 0.86, 0.98);
    let violet_col = vec3<f32>(0.58, 0.22, 0.98);
    let crystal_col = mix(cyan_col, violet_col, smoothstep(0.28, 0.78, facet));

    mat.albedo = mix(vec3<f32>(0.06, 0.12, 0.20), crystal_col * 0.85, 0.45 + 0.55 * vein);
    mat.roughness = mix(0.24, 0.12, vein);
    mat.metallic = 0.15;
    mat.f0 = 0.12;
    mat.cavity = 0.75 + 0.25 * (1.0 - vein);
    mat.emissive = crystal_col * (0.48 + 1.35 * vein) * pulse;
    mat.bump_grad = macro_n.yzw * 0.5 + micro_n.yzw * 1.1;
    mat.snow_mask = 0.0;
    return mat;
}

fn ggx_lighting(
    n: vec3<f32>,
    v: vec3<f32>,
    l: vec3<f32>,
    albedo: vec3<f32>,
    roughness: f32,
    metallic: f32,
    f0_scalar: f32,
    sun_radiance: vec3<f32>,
) -> vec3<f32> {
    let n_dot_l = max(dot(n, l), 0.0);
    if (n_dot_l <= 0.0) {
        return vec3<f32>(0.0);
    }
    let n_dot_v = max(dot(n, v), 0.001);
    let h = normalize(l + v);
    let n_dot_h = max(dot(n, h), 0.0);
    let l_dot_h = max(dot(l, h), 0.0);

    let a = max(roughness * roughness, 0.006);
    let a2 = a * a;
    let denom = n_dot_h * n_dot_h * (a2 - 1.0) + 1.0;
    let d_ggx = a2 / (PI * denom * denom + 1e-5);

    let r1 = roughness + 1.0;
    let k = (r1 * r1) / 8.0;
    let g_v = n_dot_v / (n_dot_v * (1.0 - k) + k);
    let g_l = n_dot_l / (n_dot_l * (1.0 - k) + k);
    let g_smith = g_v * g_l;

    let f0 = mix(vec3<f32>(f0_scalar), albedo, metallic);
    let fresnel = f0 + (vec3<f32>(1.0) - f0) * pow(1.0 - l_dot_h, 5.0);

    let spec = (d_ggx * g_smith * fresnel) / (4.0 * n_dot_v * n_dot_l + 1e-4);
    let kd = (vec3<f32>(1.0) - fresnel) * (1.0 - metallic);
    let diff = kd * albedo / PI;

    return (diff + spec) * sun_radiance * n_dot_l;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let n_geom = normalize(in.normal);
    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
    let sun_vis = smoothstep(-0.06, 0.14, sun.y);

    let view_vec = frame.camera_pos.xyz - in.world_pos;
    let dist = length(view_vec);
    let v = view_vec / max(dist, 1e-4);

    var mat = eval_material(in.biome, in.world_pos, n_geom, dist);

    // Optional LOD visualization tint
    if (frame.params.y > 0.5) {
        let lod_color = select(vec3<f32>(0.18, 0.85, 0.55), vec3<f32>(0.96, 0.56, 0.16), chunk.lod_info.x > 0.5);
        mat.albedo = mix(mat.albedo, lod_color, 0.42);
    }

    // Distance-faded surface-gradient bump mapping
    let bump_fade = 1.0 / (1.0 + dist * 0.022);
    let surf_grad = mat.bump_grad - dot(mat.bump_grad, n_geom) * n_geom;
    let n = normalize(n_geom - surf_grad * 0.24 * bump_fade);

    // Deep contact AO + micro-cavity shading
    let ao_pow = pow(clamp(in.ao, 0.0, 1.0), 1.65);
    let amb_ao = mix(0.14, 1.0, ao_pow) * mat.cavity;
    let sun_occ = mix(0.32, 1.0, pow(clamp(in.ao, 0.0, 1.0), 1.35)) * mix(0.55, 1.0, mat.cavity);

    // Direct Cook-Torrance GGX sunlight
    let sun_col = mix(vec3<f32>(3.35, 3.05, 2.55), vec3<f32>(3.85, 1.65, 0.68), sunset_t) * sun_vis;
    let direct = ggx_lighting(n, v, sun, mat.albedo, mat.roughness, mat.metallic, mat.f0, sun_col) * sun_occ;

    // Warm/cool split hemisphere ambient + sunlit terrain bounce
    let sky_top = mix(vec3<f32>(0.36, 0.56, 0.92), vec3<f32>(0.34, 0.26, 0.50), sunset_t);
    let warm_bounce = mix(vec3<f32>(0.24, 0.18, 0.12), vec3<f32>(0.30, 0.14, 0.08), sunset_t) * (0.4 + 0.6 * sun_vis);
    let sky_hemi = mix(warm_bounce, sky_top, n.y * 0.5 + 0.5);
    let ambient = mat.albedo * sky_hemi * 0.68 * amb_ao;

    // Snow cool icy subsurface scatter in shadows + sun micro-sparkle glints
    var snow_extra = vec3<f32>(0.0);
    if (mat.snow_mask > 0.5) {
        let wrap_l = max((dot(n, sun) + 0.35) / 1.35, 0.0);
        let icy_sss = vec3<f32>(0.38, 0.62, 0.96) * (1.0 - max(dot(n, sun), 0.0)) * wrap_l * 0.28 * amb_ao;
        snow_extra += icy_sss * sun_vis;

        if (dist < 60.0) {
            let cell = floor(in.world_pos * 13.5);
            let rnd = normalize(hash33(cell) * 2.0 - vec3<f32>(1.0));
            let spark_n = normalize(n + rnd * 0.55);
            let h_vec = normalize(sun + v);
            let glints = pow(max(dot(spark_n, h_vec), 0.0), 190.0) * (1.0 - smoothstep(8.0, 60.0, dist));
            snow_extra += sun_col * glints * 1.45 * max(dot(n, sun), 0.0);
        }
    }

    // Grazing sky Fresnel reflection + crisp warm sun rim light
    let n_dot_v = max(dot(n, v), 0.0);
    let fresnel_view = mat.f0 + (1.0 - mat.f0) * pow(1.0 - n_dot_v, 5.0);
    let refl_dir = reflect(-v, n);
    let env_sky = mix(warm_bounce, sky_top, clamp(refl_dir.y * 0.5 + 0.5, 0.0, 1.0));
    let env_spec = env_sky * fresnel_view * (1.0 - mat.roughness * 0.78) * amb_ao * 0.65;

    let sun_rim_dir = pow(max(dot(-v, sun), 0.0), 2.5);
    let rim_mask = pow(1.0 - n_dot_v, 3.8) * (0.35 + 0.65 * max(dot(n, sun) * 0.5 + 0.5, 0.0));
    let rim_col = mix(vec3<f32>(1.05, 0.92, 0.72), vec3<f32>(1.45, 0.62, 0.24), sunset_t);
    let rim_light = rim_col * rim_mask * (0.14 + 0.48 * sun_rim_dir) * sun_vis * amb_ao;

    // Pulsing bioluminescent crystal Fresnel rim
    let crystal_rim = mat.emissive * (0.75 + 0.95 * pow(1.0 - n_dot_v, 2.2));

    var color = direct + ambient + env_spec + rim_light + snow_extra + crystal_rim;

    // Atmospheric aerial perspective fog with sun Mie in-scattering
    let fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
    let fog_base = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.32, 0.12), sunset_t);
    let mie_fog = pow(max(dot(-v, sun), 0.0), 4.0) * (0.28 + 0.65 * sunset_t) * sun_vis;
    let fog_color = fog_base + vec3<f32>(1.10, 0.48, 0.16) * mie_fog;
    color = mix(color, fog_color, clamp(fog_t, 0.0, 0.94));

    // ACES filmic tone mapping + gamma
    color = (color * (2.51 * color + vec3<f32>(0.03))) / (color * (2.43 * color + vec3<f32>(0.59)) + vec3<f32>(0.14));
    return vec4<f32>(pow(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1.0 / 2.2)), 1.0);
}
`;

const WATER_WGSL = /* wgsl */ `
struct FrameUniforms {
    view_proj: mat4x4<f32>,
    inv_view_proj: mat4x4<f32>,
    camera_pos: vec4<f32>,
    sun_dir: vec4<f32>,
    params: vec4<f32>, // x = time, y = lod_tint
};

@group(0) @binding(0) var<uniform> frame: FrameUniforms;

struct WaterOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0) world_pos: vec3<f32>,
};

const WATER_LEVEL: f32 = -6.0;
const WATER_EXTENT: f32 = 560.0;

@vertex
fn vs_water(@builtin(vertex_index) vid: u32) -> WaterOut {
    var quad = array<vec2<f32>, 6>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0,  1.0),
    );
    let q = quad[vid];
    let world = vec3<f32>(
        frame.camera_pos.x + q.x * WATER_EXTENT,
        WATER_LEVEL,
        frame.camera_pos.z + q.y * WATER_EXTENT,
    );
    return WaterOut(frame.view_proj * vec4<f32>(world, 1.0), world);
}

fn w_hash21(p: vec2<f32>) -> f32 {
    var p3 = fract(vec3<f32>(p.xyx) * 0.1031);
    p3 += vec3<f32>(dot(p3, p3.yzx + vec3<f32>(33.33)));
    return fract((p3.x + p3.y) * p3.z);
}

fn w_vnoise(p: vec2<f32>) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = w_hash21(i);
    let b = w_hash21(i + vec2<f32>(1.0, 0.0));
    let c = w_hash21(i + vec2<f32>(0.0, 1.0));
    let d = w_hash21(i + vec2<f32>(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

fn eval_sky_linear(dir: vec3<f32>, sun: vec3<f32>, sunset_t: f32, sun_vis: f32, t: f32) -> vec3<f32> {
    let horizon = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.32, 0.12), sunset_t);
    let zenith = mix(vec3<f32>(0.075, 0.21, 0.56), vec3<f32>(0.085, 0.105, 0.29), sunset_t);
    let y = clamp(dir.y, 0.02, 1.0);
    var sky = mix(horizon, zenith, pow(y, 0.52));
    let sun_dot = max(dot(dir, sun), 0.0);
    let glow_col = mix(vec3<f32>(1.15, 0.90, 0.60), vec3<f32>(1.55, 0.52, 0.16), sunset_t);
    sky += glow_col * pow(sun_dot, 8.0) * (0.30 + 0.36 * sunset_t) * sun_vis;
    sky += glow_col * smoothstep(0.996, 0.9996, sun_dot) * 2.4 * sun_vis;

    // Subtle reflected cloud wisps
    if (dir.y > 0.02) {
        let uv = dir.xz / max(dir.y + 0.14, 0.05) * 0.48 + vec2<f32>(t * 0.018, t * 0.007);
        let c_raw = w_vnoise(uv) * 0.65 + w_vnoise(uv * 2.3 + vec2<f32>(4.1)) * 0.35;
        let c_mask = smoothstep(0.52, 0.80, c_raw) * smoothstep(0.02, 0.18, dir.y);
        let c_col = mix(vec3<f32>(1.18, 1.18, 1.20), vec3<f32>(1.45, 0.70, 0.38), sunset_t);
        sky = mix(sky, c_col, c_mask * 0.68);
    }
    return sky;
}

@fragment
fn fs_water(in: WaterOut) -> @location(0) vec4<f32> {
    let t = frame.params.x;
    let p = in.world_pos.xz;
    let view_vec = frame.camera_pos.xyz - in.world_pos;
    let dist = length(view_vec);
    let v = view_vec / max(dist, 1e-4);

    // Attenuate high-frequency ripple steepness at distance to prevent horizon shimmer
    let ripple_fade = 1.0 / (1.0 + dist * 0.0085);

    // Multi-frequency directional wave octaves with analytical partial derivatives
    var dhdx = 0.0;
    var dhdz = 0.0;
    var wave_h = 0.0;

    // Octave 1: Broad swell
    {
        let d = vec2<f32>(0.82, 0.57);
        let freq = 0.22;
        let amp = 0.24 * ripple_fade;
        let phase = dot(d, p) * freq + t * 1.15;
        wave_h += amp * sin(phase);
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }
    // Octave 2: Cross swell
    {
        let d = vec2<f32>(-0.55, 0.83);
        let freq = 0.37;
        let amp = 0.15 * ripple_fade;
        let phase = dot(d, p) * freq + t * 1.48;
        wave_h += amp * sin(phase);
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }
    // Octave 3: Medium wind chop
    {
        let d = vec2<f32>(0.36, -0.93);
        let freq = 0.85;
        let amp = 0.065 * ripple_fade;
        let phase = dot(d, p) * freq + t * 2.15;
        wave_h += amp * sin(phase);
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }
    // Octave 4: Fine capillary ripples
    {
        let d = vec2<f32>(-0.91, -0.41);
        let freq = 1.75;
        let amp = 0.032 * ripple_fade;
        let phase = dot(d, p) * freq + t * 2.85;
        wave_h += amp * sin(phase);
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }
    // Octave 5: Crisp micro-sparkle ripples
    {
        let d = vec2<f32>(0.68, -0.73);
        let freq = 3.20;
        let amp = 0.015 * ripple_fade * ripple_fade;
        let phase = dot(d, p) * freq - t * 3.40;
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }

    let n_sign = select(-1.0, 1.0, frame.camera_pos.y >= WATER_LEVEL);
    let n = normalize(vec3<f32>(-dhdx, n_sign, -dhdz));

    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
    let sun_vis = smoothstep(-0.06, 0.14, sun.y);

    // Schlick Fresnel (F0 = 0.02 for water)
    let n_dot_v = max(dot(n, v), 0.0);
    let fresnel = 0.02 + 0.98 * pow(1.0 - n_dot_v, 5.0);

    // Deep teal body color with turquoise subsurface crest scatter
    let deep_teal = vec3<f32>(0.012, 0.10, 0.16);
    let crest_teal = vec3<f32>(0.032, 0.28, 0.34);
    let sun_diffuse = max(dot(n, sun), 0.0);
    let crest_t = clamp(wave_h * 1.45 + 0.35 + sun_diffuse * 0.35, 0.0, 1.0);
    let sss = pow(max(dot(v, -sun + n * 0.55), 0.0), 3.0) * clamp(wave_h * 2.4 + 0.35, 0.0, 1.0);
    let sun_tint = mix(vec3<f32>(1.0, 0.96, 0.90), vec3<f32>(1.18, 0.68, 0.38), sunset_t);
    var water_body = mix(deep_teal, crest_teal, crest_t) * (0.42 + 0.58 * sun_vis) * sun_tint;
    water_body += vec3<f32>(0.05, 0.38, 0.40) * sss * sun_vis;

    // Sky + cloud reflection
    let refl_dir = reflect(-v, n);
    let sky_refl = eval_sky_linear(refl_dir, sun, sunset_t, sun_vis, t);

    var color = mix(water_body, sky_refl, clamp(fresnel * 1.06, 0.04, 0.96));

    // Subtle wind-crest foam wisps
    let foam_n = w_vnoise(p * 0.65 + vec2<f32>(t * 0.35, -t * 0.22));
    let crest_foam = smoothstep(0.22, 0.40, wave_h) * smoothstep(0.54, 0.80, foam_n) * ripple_fade;
    let foam_col = mix(vec3<f32>(0.88, 0.94, 0.98), vec3<f32>(1.05, 0.75, 0.52), sunset_t) * (0.4 + 0.6 * sun_vis);
    color = mix(color, foam_col, crest_foam * 0.45);

    // Crisp sun specular sparkle + warm sun glitter path
    let half_vec = normalize(sun + v);
    let n_dot_h = max(dot(n, half_vec), 0.0);
    let sparkle_col = mix(vec3<f32>(1.45, 1.32, 1.10), vec3<f32>(1.85, 0.90, 0.38), sunset_t);
    let spec_glint = pow(n_dot_h, 48.0) * 0.22 + pow(n_dot_h, 140.0) * 0.72 + pow(n_dot_h, 640.0) * 4.2;
    color += sparkle_col * spec_glint * sun_vis;

    // Distance fog matching terrain & sky exactly
    let fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
    let fog_base = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.32, 0.12), sunset_t);
    let mie_fog = pow(max(dot(-v, sun), 0.0), 4.0) * (0.28 + 0.65 * sunset_t) * sun_vis;
    let fog_color = fog_base + vec3<f32>(1.10, 0.48, 0.16) * mie_fog;
    color = mix(color, fog_color, clamp(fog_t, 0.0, 0.94));

    // ACES filmic tone mapping + gamma matching terrain
    color = (color * (2.51 * color + vec3<f32>(0.03))) / (color * (2.43 * color + vec3<f32>(0.59)) + vec3<f32>(0.14));
    color = pow(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1.0 / 2.2));

    let alpha = mix(mix(0.62, 0.95, fresnel), 0.98, clamp(fog_t, 0.0, 1.0));
    return vec4<f32>(color, alpha);
}
`;

export class WorldRenderer {
  private device!: GPUDevice;
  private context!: GPUCanvasContext;
  private format!: GPUTextureFormat;
  private depthTexture!: GPUTexture;
  private skyPipeline!: GPURenderPipeline;
  private terrainPipeline!: GPURenderPipeline;
  private waterPipeline!: GPURenderPipeline;
  private frameUniformBuffer!: GPUBuffer;
  private frameBindGroup!: GPUBindGroup;
  private chunkBindGroupLayout!: GPUBindGroupLayout;
  private chunks = new Map<string, GpuChunkEntry>();
  private totalTriangles = 0;
  public lodTint = false;
  public sunDir: Vec3 = [0.55, 0.72, 0.42];
  public readonly backendName = "WebGPU";

  constructor(private canvas: HTMLCanvasElement) {}

  async init(): Promise<void> {
    if (!navigator.gpu) {
      throw new Error("WebGPU is not supported in this browser. Use Chrome/Edge/Chromium with WebGPU enabled.");
    }
    const adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) {
      throw new Error("Failed to acquire a browser WebGPU adapter.");
    }
    this.device = await adapter.requestDevice();

    const ctx = this.canvas.getContext("webgpu");
    if (!ctx) throw new Error("Failed to create WebGPU canvas context.");
    this.context = ctx;
    this.format = navigator.gpu.getPreferredCanvasFormat();

    this.resize();

    const frameBgl = this.device.createBindGroupLayout({
      entries: [
        {
          binding: 0,
          visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
          buffer: { type: "uniform" },
        },
      ],
    });

    this.chunkBindGroupLayout = this.device.createBindGroupLayout({
      entries: [
        {
          binding: 0,
          visibility: GPUShaderStage.VERTEX | GPUShaderStage.FRAGMENT,
          buffer: { type: "uniform" },
        },
      ],
    });

    this.frameUniformBuffer = this.device.createBuffer({
      size: 16 * 4 * 2 + 16 * 3, // 2 mat4 + 3 vec4 = 176 bytes
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });

    this.frameBindGroup = this.device.createBindGroup({
      layout: frameBgl,
      entries: [{ binding: 0, resource: { buffer: this.frameUniformBuffer } }],
    });

    const skyModule = this.device.createShaderModule({ code: SKY_WGSL });
    this.skyPipeline = this.device.createRenderPipeline({
      layout: this.device.createPipelineLayout({ bindGroupLayouts: [frameBgl] }),
      vertex: { module: skyModule, entryPoint: "vs_sky" },
      fragment: { module: skyModule, entryPoint: "fs_sky", targets: [{ format: this.format }] },
      primitive: { topology: "triangle-list" },
      depthStencil: { format: "depth24plus", depthWriteEnabled: false, depthCompare: "always" },
    });

    const terrainModule = this.device.createShaderModule({ code: TERRAIN_WGSL });
    this.terrainPipeline = this.device.createRenderPipeline({
      layout: this.device.createPipelineLayout({
        bindGroupLayouts: [frameBgl, this.chunkBindGroupLayout],
      }),
      vertex: {
        module: terrainModule,
        entryPoint: "vs_main",
        buffers: [
          {
            arrayStride: 32,
            attributes: [
              { shaderLocation: 0, offset: 0, format: "float32x3" },
              { shaderLocation: 1, offset: 12, format: "float32" },
              { shaderLocation: 2, offset: 16, format: "float32x3" },
              { shaderLocation: 3, offset: 28, format: "float32" },
            ],
          },
        ],
      },
      fragment: {
        module: terrainModule,
        entryPoint: "fs_main",
        targets: [{ format: this.format }],
      },
      primitive: {
        topology: "triangle-list",
        cullMode: "back",
        frontFace: "ccw",
      },
      depthStencil: {
        format: "depth24plus",
        depthWriteEnabled: true,
        depthCompare: "less",
      },
    });

    const waterModule = this.device.createShaderModule({ code: WATER_WGSL });
    this.waterPipeline = this.device.createRenderPipeline({
      layout: this.device.createPipelineLayout({ bindGroupLayouts: [frameBgl] }),
      vertex: {
        module: waterModule,
        entryPoint: "vs_water",
      },
      fragment: {
        module: waterModule,
        entryPoint: "fs_water",
        targets: [
          {
            format: this.format,
            blend: {
              color: {
                srcFactor: "src-alpha",
                dstFactor: "one-minus-src-alpha",
                operation: "add",
              },
              alpha: {
                srcFactor: "one",
                dstFactor: "one-minus-src-alpha",
                operation: "add",
              },
            },
          },
        ],
      },
      primitive: {
        topology: "triangle-list",
        cullMode: "none",
      },
      depthStencil: {
        format: "depth24plus",
        depthWriteEnabled: false,
        depthCompare: "less",
      },
    });
  }

  resize(): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.floor(window.innerWidth * dpr));
    const h = Math.max(1, Math.floor(window.innerHeight * dpr));
    if (this.canvas.width === w && this.canvas.height === h && this.depthTexture) {
      return;
    }
    this.canvas.width = w;
    this.canvas.height = h;
    this.context.configure({
      device: this.device,
      format: this.format,
      alphaMode: "opaque",
    });
    this.depthTexture?.destroy();
    this.depthTexture = this.device.createTexture({
      size: [w, h],
      format: "depth24plus",
      usage: GPUTextureUsage.RENDER_ATTACHMENT,
    });
  }

  upsertChunk(chunk: DecodedChunk): void {
    const key = chunkKey(chunk.coord);
    this.deleteChunkByKey(key);

    let vertexBuffer: GPUBuffer | null = null;
    let indexBuffer: GPUBuffer | null = null;
    if (chunk.vertexCount > 0 && chunk.indexCount > 0) {
      vertexBuffer = this.device.createBuffer({
        size: Math.max(4, (chunk.vertexBytes.byteLength + 3) & ~3),
        usage: GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST,
      });
      this.device.queue.writeBuffer(vertexBuffer, 0, chunk.vertexBytes);

      indexBuffer = this.device.createBuffer({
        size: Math.max(4, (chunk.indexBytes.byteLength + 3) & ~3),
        usage: GPUBufferUsage.INDEX | GPUBufferUsage.COPY_DST,
      });
      this.device.queue.writeBuffer(indexBuffer, 0, chunk.indexBytes);
    }

    const uniformBuffer = this.device.createBuffer({
      size: 16,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    this.device.queue.writeBuffer(uniformBuffer, 0, new Float32Array([chunk.coord.lod, 0, 0, 0]));

    const bindGroup = this.device.createBindGroup({
      layout: this.chunkBindGroupLayout,
      entries: [{ binding: 0, resource: { buffer: uniformBuffer } }],
    });

    const triangleCount = Math.floor(chunk.indexCount / 3);
    this.totalTriangles += triangleCount;
    this.chunks.set(key, {
      vertexBuffer,
      indexBuffer,
      indexCount: chunk.indexCount,
      triangleCount,
      lod: chunk.coord.lod,
      uniformBuffer,
      bindGroup,
    });
  }

  evictChunks(coords: ChunkCoord[]): void {
    for (const c of coords) {
      this.deleteChunkByKey(chunkKey(c));
    }
  }

  clearAllChunks(): void {
    for (const key of Array.from(this.chunks.keys())) {
      this.deleteChunkByKey(key);
    }
  }

  private deleteChunkByKey(key: string): void {
    const existing = this.chunks.get(key);
    if (!existing) return;
    existing.vertexBuffer?.destroy();
    existing.indexBuffer?.destroy();
    existing.uniformBuffer.destroy();
    this.totalTriangles -= existing.triangleCount;
    this.chunks.delete(key);
  }

  getChunkCount(): number {
    return this.chunks.size;
  }

  getTriangleCount(): number {
    return this.totalTriangles;
  }

  getAspect(): number {
    return this.canvas.width / Math.max(1, this.canvas.height);
  }

  render(eye: Vec3, dir: Vec3, timeSec: number): void {
    const aspect = this.getAspect();
    const proj = perspectiveZO((62 * Math.PI) / 180, aspect, 0.3, 600.0);
    const view = lookAt(eye, [eye[0] + dir[0], eye[1] + dir[1], eye[2] + dir[2]], [0, 1, 0]);
    const viewProj = multiplyMat4(proj, view);
    const invViewProj = invertMat4(viewProj);

    const sunLen = Math.hypot(this.sunDir[0], this.sunDir[1], this.sunDir[2]) || 1;
    const sx = this.sunDir[0] / sunLen;
    const sy = this.sunDir[1] / sunLen;
    const sz = this.sunDir[2] / sunLen;

    const frameData = new Float32Array(44);
    frameData.set(viewProj, 0);
    frameData.set(invViewProj, 16);
    frameData.set([eye[0], eye[1], eye[2], 1.0], 32);
    frameData.set([sx, sy, sz, 0.0], 36);
    frameData.set([timeSec, this.lodTint ? 1.0 : 0.0, 0.0, 0.0], 40);
    this.device.queue.writeBuffer(this.frameUniformBuffer, 0, frameData);

    const encoder = this.device.createCommandEncoder();
    const pass = encoder.beginRenderPass({
      colorAttachments: [
        {
          view: this.context.getCurrentTexture().createView(),
          loadOp: "clear",
          storeOp: "store",
          clearValue: { r: 0.08, g: 0.14, b: 0.24, a: 1.0 },
        },
      ],
      depthStencilAttachment: {
        view: this.depthTexture.createView(),
        depthClearValue: 1.0,
        depthLoadOp: "clear",
        depthStoreOp: "store",
      },
    });

    // 1. Procedural Sky Dome
    pass.setPipeline(this.skyPipeline);
    pass.setBindGroup(0, this.frameBindGroup);
    pass.draw(3);

    // 2. Streamed Procedural Chunks
    pass.setPipeline(this.terrainPipeline);
    pass.setBindGroup(0, this.frameBindGroup);
    for (const chunk of this.chunks.values()) {
      if (!chunk.vertexBuffer || !chunk.indexBuffer || chunk.indexCount === 0) continue;
      pass.setBindGroup(1, chunk.bindGroup);
      pass.setVertexBuffer(0, chunk.vertexBuffer);
      pass.setIndexBuffer(chunk.indexBuffer, "uint32");
      pass.drawIndexed(chunk.indexCount);
    }

    // 3. Animated Procedural Water Plane (y = -6.0, transparent with Fresnel + wave normals)
    pass.setPipeline(this.waterPipeline);
    pass.setBindGroup(0, this.frameBindGroup);
    pass.draw(6);

    pass.end();
    this.device.queue.submit([encoder.finish()]);
  }
}
