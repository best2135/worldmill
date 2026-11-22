import { invertMat4, lookAt, multiplyMat4, perspectiveNO, type Vec3 } from "./math";
import { chunkKey, type ChunkCoord, type DecodedChunk } from "./protocol";

interface GlChunkEntry {
  vao: WebGLVertexArrayObject | null;
  vbo: WebGLBuffer | null;
  ibo: WebGLBuffer | null;
  indexCount: number;
  triangleCount: number;
  lod: number;
}

const SKY_VS = `#version 300 es
precision highp float;
out vec2 v_ndc;
void main() {
  vec2 pos[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
  v_ndc = pos[gl_VertexID];
  gl_Position = vec4(v_ndc, 0.9999, 1.0);
}
`;

const SKY_FS = `#version 300 es
precision highp float;
in vec2 v_ndc;
out vec4 fragColor;
uniform mat4 u_inv_view_proj;
uniform vec3 u_camera_pos;
uniform vec3 u_sun_dir;
uniform float u_time;

float sky_hash21(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

float sky_vnoise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  float a = sky_hash21(i);
  float b = sky_hash21(i + vec2(1.0, 0.0));
  float c = sky_hash21(i + vec2(0.0, 1.0));
  float d = sky_hash21(i + vec2(1.0, 1.0));
  return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float sky_fbm5(vec2 p) {
  float v = 0.0;
  float a = 0.5;
  mat2 rot = mat2(1.624, 1.218, -1.218, 1.624);
  for (int i = 0; i < 5; i++) {
    v += a * sky_vnoise(p);
    p = rot * p + vec2(13.7, 29.3);
    a *= 0.5;
  }
  return v;
}

float sky_fbm3(vec2 p) {
  float v = 0.0;
  float a = 0.5;
  mat2 rot = mat2(1.624, 1.218, -1.218, 1.624);
  for (int i = 0; i < 3; i++) {
    v += a * sky_vnoise(p);
    p = rot * p + vec2(7.1, 19.4);
    a *= 0.5;
  }
  return v;
}

float cloud_density(vec2 uv) {
  vec2 warp = vec2(
    sky_fbm3(uv * 0.45 + vec2(4.2, 1.7)),
    sky_fbm3(uv * 0.45 + vec2(9.1, 6.3))
  );
  float base = sky_fbm5(uv + (warp - 0.5) * 0.65);
  float billow = 1.0 - abs(sky_fbm3(uv * 2.1 + 3.4) * 2.0 - 1.0);
  float raw = mix(base, base * 0.72 + billow * 0.34, 0.38);
  return smoothstep(0.47, 0.78, raw);
}

void main() {
  vec4 world_h = u_inv_view_proj * vec4(v_ndc, 1.0, 1.0);
  vec3 dir = normalize(world_h.xyz / world_h.w - u_camera_pos);
  vec3 sun = normalize(u_sun_dir);
  float sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
  float sun_vis = smoothstep(-0.08, 0.14, sun.y);

  vec3 horizon_base = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.32, 0.12), sunset_t);
  vec3 zenith  = mix(vec3(0.075, 0.21, 0.56), vec3(0.085, 0.105, 0.29), sunset_t);
  vec3 ground  = mix(vec3(0.10, 0.13, 0.19), vec3(0.18, 0.09, 0.09), sunset_t);

  float sun_dot = max(dot(dir, sun), 0.0);
  float mie_horizon = pow(sun_dot, 4.0) * exp(-abs(dir.y) * 4.5);
  vec3 horizon = horizon_base + vec3(1.10, 0.48, 0.16) * mie_horizon * (0.28 + 0.65 * sunset_t) * sun_vis;

  vec3 sky = dir.y >= 0.0
    ? mix(horizon, zenith, pow(max(dir.y, 0.0), 0.52))
    : mix(ground, horizon, clamp(dir.y * 3.8 + 1.0, 0.0, 1.0));

  vec3 glow_col = mix(vec3(1.15, 0.90, 0.60), vec3(1.55, 0.52, 0.16), sunset_t);
  vec3 disc_col = mix(vec3(2.80, 2.55, 2.15), vec3(2.95, 1.65, 0.65), sunset_t);
  sky += glow_col * pow(sun_dot, 8.0) * (0.24 + 0.36 * sunset_t) * sun_vis;
  sky += glow_col * pow(sun_dot, 48.0) * 0.55 * sun_vis;
  sky += disc_col * smoothstep(0.9988, 0.9997, sun_dot) * 2.4 * sun_vis;

  if (dir.y > 0.005) {
    float R_earth = 2400.0;
    float H_cloud = 24.0;
    float ray_dist = -R_earth * dir.y + sqrt(R_earth * R_earth * dir.y * dir.y + 2.0 * R_earth * H_cloud + H_cloud * H_cloud);
    vec3 dome_pos = dir * ray_dist;
    vec2 wind = vec2(u_time * 0.018, u_time * 0.007);
    vec2 cloud_uv = dome_pos.xz * 0.011 + wind;

    vec2 cirrus_uv = dome_pos.xz * 0.0065 * vec2(1.0, 2.2) + wind * 1.4;
    float cirrus = smoothstep(0.54, 0.84, sky_fbm5(cirrus_uv)) * 0.36;
    vec3 cirrus_col = mix(vec3(0.92, 0.96, 1.02), vec3(1.32, 0.72, 0.45), sunset_t);

    float dens = cloud_density(cloud_uv);
    vec2 sun_horiz = normalize(vec3(sun.x, 0.0, sun.z) + vec3(1e-4, 0.0, 0.0)).xz;
    float s1 = cloud_density(cloud_uv + sun_horiz * 0.09);
    float s2 = cloud_density(cloud_uv + sun_horiz * 0.22);
    float light_trans = exp(-(s1 * 0.65 + s2 * 0.35) * 2.35);

    vec3 cloud_shadow = mix(vec3(0.28, 0.36, 0.50), vec3(0.34, 0.24, 0.38), sunset_t);
    vec3 cloud_lit = mix(vec3(1.28, 1.24, 1.18), vec3(1.65, 0.78, 0.40), sunset_t);
    float hg_forward = pow(sun_dot, 7.0) * (1.0 - dens * 0.65) * 0.85;
    vec3 cloud_col = mix(cloud_shadow, cloud_lit, clamp(light_trans * 1.15, 0.0, 1.0));
    cloud_col += glow_col * hg_forward * sun_vis;

    float horizon_fade = smoothstep(0.01, 0.16, dir.y);
    sky = mix(sky, cirrus_col, cirrus * horizon_fade * 0.75);
    sky = mix(sky, cloud_col, dens * horizon_fade * 0.92);
  }

  sky = (sky * (2.51 * sky + 0.03)) / (sky * (2.43 * sky + 0.59) + 0.14);
  sky = pow(clamp(sky, 0.0, 1.0), vec3(1.0 / 2.2));
  fragColor = vec4(sky, 1.0);
}
`;

const TERRAIN_VS = `#version 300 es
precision highp float;
layout(location = 0) in vec3 a_pos;
layout(location = 1) in float a_ao;
layout(location = 2) in vec3 a_normal;
layout(location = 3) in float a_biome;

uniform mat4 u_view_proj;

out vec3 v_world_pos;
out vec3 v_normal;
out float v_ao;
out float v_biome;

void main() {
  gl_Position = u_view_proj * vec4(a_pos, 1.0);
  v_world_pos = a_pos;
  v_normal = a_normal;
  v_ao = a_ao;
  v_biome = a_biome;
}
`;

const TERRAIN_FS = `#version 300 es
precision highp float;
in vec3 v_world_pos;
in vec3 v_normal;
in float v_ao;
in float v_biome;
out vec4 fragColor;

uniform vec3 u_camera_pos;
uniform vec3 u_sun_dir;
uniform float u_lod_tint;
uniform float u_chunk_lod;
uniform float u_time;

const float PI = 3.14159265;
const float WATER_LEVEL = -6.0;

float hash21(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.xx + p3.yz) * p3.zy);
}

vec3 hash33(vec3 p) {
  p = fract(p * vec3(0.1031, 0.1030, 0.0973));
  p += dot(p, p.yxz + 33.33);
  return fract((p.xxy + p.yxx) * p.zyx);
}

vec3 vnoise_d(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  vec2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);

  float a = hash21(i);
  float b = hash21(i + vec2(1.0, 0.0));
  float c = hash21(i + vec2(0.0, 1.0));
  float d = hash21(i + vec2(1.0, 1.0));

  float k1 = b - a;
  float k2 = c - a;
  float k3 = a - b - c + d;
  float val = a + k1 * u.x + k2 * u.y + k3 * u.x * u.y;
  vec2 grad = vec2(k1 + k3 * u.y, k2 + k3 * u.x) * du;
  return vec3(val, grad.x, grad.y);
}

vec3 fbm4_d(vec2 p) {
  float val = 0.0;
  vec2 grad = vec2(0.0);
  float amp = 0.5;
  mat2 step_m = mat2(1.624, 1.218, -1.218, 1.624);
  mat2 step_t = mat2(1.624, -1.218, 1.218, 1.624);
  mat2 mt = mat2(1.0, 0.0, 0.0, 1.0);
  for (int i = 0; i < 4; i++) {
    vec3 n = vnoise_d(p);
    val += amp * n.x;
    grad += amp * (mt * n.yz);
    p = step_m * p + vec2(17.1, 31.7);
    mt = step_t * mt;
    amp *= 0.5;
  }
  return vec3(val, grad.x, grad.y);
}

vec3 voronoi2d(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  float d1 = 8.0;
  float d2 = 8.0;
  float id = 0.0;
  for (int y = -1; y <= 1; y++) {
    for (int x = -1; x <= 1; x++) {
      vec2 g = vec2(float(x), float(y));
      vec2 o = hash22(i + g);
      vec2 r = g + o - f;
      float d = dot(r, r);
      if (d < d1) {
        d2 = d1;
        d1 = d;
        id = hash21(i + g);
      } else if (d < d2) {
        d2 = d;
      }
    }
  }
  float sd1 = sqrt(d1);
  float sd2 = sqrt(d2);
  return vec3(sd1, sd2 - sd1, id);
}

vec3 triplanar_weights(vec3 n) {
  vec3 w = pow(abs(n), vec3(4.0));
  return w / max(w.x + w.y + w.z, 1e-4);
}

vec4 triplanar_fbm4_d(vec3 wp, vec3 w, float scale) {
  vec3 pyz = fbm4_d(wp.zy * scale + vec2(3.1, 7.7));
  vec3 pxz = fbm4_d(wp.xz * scale + vec2(19.3, 11.9));
  vec3 pxy = fbm4_d(wp.xy * scale + vec2(41.7, 23.5));

  float val = pyz.x * w.x + pxz.x * w.y + pxy.x * w.z;
  vec3 g_yz = vec3(0.0, pyz.z, pyz.y) * scale;
  vec3 g_xz = vec3(pxz.y, 0.0, pxz.z) * scale;
  vec3 g_xy = vec3(pxy.y, pxy.z, 0.0) * scale;
  vec3 grad = g_yz * w.x + g_xz * w.y + g_xy * w.z;
  return vec4(val, grad.x, grad.y, grad.z);
}

vec3 triplanar_voronoi(vec3 wp, vec3 w, float scale) {
  vec3 acc = vec3(0.0);
  if (w.x > 0.02) acc += voronoi2d(wp.zy * scale + vec2(5.2, 8.4)) * w.x;
  if (w.y > 0.02) acc += voronoi2d(wp.xz * scale + vec2(14.7, 2.9)) * w.y;
  if (w.z > 0.02) acc += voronoi2d(wp.xy * scale + vec2(29.1, 17.6)) * w.z;
  return acc;
}

struct MatSample {
  vec3 albedo;
  float roughness;
  float metallic;
  float f0;
  float cavity;
  vec3 emissive;
  vec3 bump_grad;
  float snow_mask;
};

MatSample eval_granite(vec3 wp, vec3 n, vec3 w, vec4 macro_n, vec4 micro_n, vec4 detail_n, vec3 vor) {
  MatSample m;
  float strata_coord = wp.y * 0.65 + macro_n.x * 2.2 + micro_n.x * 0.38;
  float strata = 0.5 + 0.5 * sin(strata_coord * 3.1 + sin(strata_coord * 1.4) * 1.2);
  vec3 strata_grad = vec3(0.0, cos(strata_coord * 3.1) * 0.65, 0.0);

  float crack = 1.0 - smoothstep(0.015, 0.14, vor.y);
  float lichen = smoothstep(0.66, 0.88, n.y) * smoothstep(0.48, 0.74, macro_n.x);
  float scree = smoothstep(0.54, 0.84, n.y) * smoothstep(0.42, 0.68, micro_n.x);

  vec3 cool_rock = vec3(0.24, 0.25, 0.29);
  vec3 mid_rock = vec3(0.41, 0.40, 0.39);
  vec3 warm_vein = vec3(0.46, 0.37, 0.30);
  vec3 lichen_col = vec3(0.22, 0.31, 0.18);

  vec3 col = mix(cool_rock, mid_rock, strata * 0.72 + macro_n.x * 0.28);
  col = mix(col, warm_vein, smoothstep(0.52, 0.80, micro_n.x) * 0.38);
  col *= 0.78 + 0.38 * detail_n.x;
  col = mix(col, col * 0.34, crack * 0.85);
  col = mix(col, lichen_col * (0.8 + 0.35 * detail_n.x), lichen * 0.72);
  col = mix(col, vec3(0.45, 0.44, 0.42), scree * 0.25);

  m.albedo = col;
  m.roughness = mix(0.64, 0.88, max(scree, lichen) * 0.75 + crack * 0.25);
  m.metallic = 0.0;
  m.f0 = 0.045;
  m.cavity = mix(1.0, 0.35, crack) * mix(0.82, 1.05, micro_n.x);
  m.emissive = vec3(0.0);
  m.bump_grad = macro_n.yzw * 0.55 + micro_n.yzw * 0.95 + detail_n.yzw * 0.45 + strata_grad * 0.28;
  m.snow_mask = 0.0;
  return m;
}

MatSample eval_material(float biome, vec3 wp, vec3 n, float dist) {
  vec3 w = triplanar_weights(n);
  vec4 macro_n = triplanar_fbm4_d(wp, w, 0.13);
  vec4 micro_n = triplanar_fbm4_d(wp, w, 0.78);
  float detail_fade = 1.0 - smoothstep(12.0, 140.0, dist);
  vec4 detail_n = detail_fade > 0.01 ? triplanar_fbm4_d(wp, w, 2.75) : vec4(0.5, 0.0, 0.0, 0.0);

  int id = int(floor(biome + 0.5));
  MatSample mat;

  if (id == 0) {
    vec3 vor = triplanar_voronoi(wp, w, 0.65);
    float crack = 1.0 - smoothstep(0.02, 0.16, vor.y);
    float damp = smoothstep(0.50, 0.72, macro_n.x);

    vec3 dark_basalt = vec3(0.075, 0.085, 0.115);
    vec3 mineral_basalt = vec3(0.16, 0.19, 0.24);
    vec3 col = mix(dark_basalt, mineral_basalt, micro_n.x * 0.65 + detail_n.x * 0.35);
    col = mix(col, col * 0.30, crack * 0.9);
    col *= mix(1.0, 0.62, damp);

    mat.albedo = col;
    mat.roughness = mix(0.80, 0.18, damp) + crack * 0.12;
    mat.metallic = 0.04;
    mat.f0 = 0.055;
    mat.cavity = mix(1.0, 0.28, crack) * (0.78 + 0.28 * micro_n.x);
    mat.emissive = vec3(0.0);
    mat.bump_grad = macro_n.yzw * 0.6 + micro_n.yzw * 1.15 + detail_n.yzw * 0.55 * detail_fade;
    mat.snow_mask = 0.0;
    return mat;
  }

  if (id == 1) {
    float strata_coord = wp.y * 0.92 + macro_n.x * 2.1 + micro_n.x * 0.35;
    float band_a = 0.5 + 0.5 * sin(strata_coord * 2.7);
    float band_b = 0.5 + 0.5 * sin(strata_coord * 6.4 + 1.3);

    vec3 sand_buff = vec3(0.76, 0.62, 0.42);
    vec3 terracotta = vec3(0.58, 0.33, 0.21);
    vec3 pale_ochre = vec3(0.85, 0.74, 0.54);

    vec3 col = mix(terracotta, sand_buff, band_a);
    col = mix(col, pale_ochre, band_b * 0.45 * macro_n.x);
    col *= 0.84 + 0.30 * detail_n.x;

    float shore_height = wp.y + (macro_n.x - 0.5) * 0.55;
    float wet_shore = 1.0 - smoothstep(WATER_LEVEL - 0.15, WATER_LEVEL + 1.55, shore_height);
    col *= mix(1.0, 0.45, wet_shore);

    mat.albedo = col;
    mat.roughness = mix(0.86, 0.13, wet_shore);
    mat.metallic = 0.0;
    mat.f0 = 0.04;
    mat.cavity = 0.82 + 0.22 * band_a;
    mat.emissive = vec3(0.0);
    vec3 strata_grad = vec3(0.0, cos(strata_coord * 2.7) * 0.25, 0.0);
    float bump_scale = mix(1.0, 0.45, wet_shore);
    mat.bump_grad = (macro_n.yzw * 0.4 + micro_n.yzw * 0.85 + detail_n.yzw * 0.48 * detail_fade + strata_grad) * bump_scale;
    mat.snow_mask = 0.0;
    return mat;
  }

  if (id == 2) {
    vec3 emerald = vec3(0.11, 0.36, 0.12);
    vec3 sunlit_moss = vec3(0.26, 0.46, 0.14);
    vec3 warm_earth = vec3(0.20, 0.21, 0.11);

    vec3 grass_col = mix(emerald, sunlit_moss, smoothstep(0.32, 0.72, macro_n.x));
    grass_col = mix(grass_col, warm_earth, smoothstep(0.62, 0.86, micro_n.x) * 0.35);
    grass_col *= 0.80 + 0.42 * detail_n.x;

    float rock_blend = 1.0 - smoothstep(0.55, 0.79, n.y + (macro_n.x - 0.5) * 0.10);
    if (rock_blend > 0.01) {
      vec3 vor = triplanar_voronoi(wp, w, 0.55);
      MatSample rock = eval_granite(wp, n, w, macro_n, micro_n, detail_n, vor);
      mat.albedo = mix(grass_col, rock.albedo, rock_blend);
      mat.roughness = mix(0.88, rock.roughness, rock_blend);
      mat.metallic = 0.0;
      mat.f0 = mix(0.035, rock.f0, rock_blend);
      mat.cavity = mix(0.85 + 0.20 * micro_n.x, rock.cavity, rock_blend);
      mat.emissive = vec3(0.0);
      vec3 grass_grad = micro_n.yzw * 0.65 + detail_n.yzw * 0.60 * detail_fade;
      mat.bump_grad = mix(grass_grad, rock.bump_grad, rock_blend);
      mat.snow_mask = 0.0;
      return mat;
    }

    mat.albedo = grass_col;
    mat.roughness = 0.88;
    mat.metallic = 0.0;
    mat.f0 = 0.035;
    mat.cavity = 0.85 + 0.20 * micro_n.x;
    mat.emissive = vec3(0.0);
    mat.bump_grad = micro_n.yzw * 0.65 + detail_n.yzw * 0.60 * detail_fade;
    mat.snow_mask = 0.0;
    return mat;
  }

  if (id == 3) {
    vec3 vor = triplanar_voronoi(wp, w, 0.55);
    return eval_granite(wp, n, w, macro_n, micro_n, detail_n, vor);
  }

  if (id == 4) {
    vec3 wind_wp = vec3(wp.x * 0.55 + wp.z * 0.35, wp.y * 1.1, wp.z * 0.85 - wp.x * 0.45);
    vec4 drift_n = triplanar_fbm4_d(wind_wp, w, 0.42);
    float sastrugi = smoothstep(0.32, 0.72, drift_n.x);

    vec3 snow_trough = vec3(0.76, 0.85, 0.95);
    vec3 snow_crest = vec3(0.95, 0.97, 1.00);
    vec3 col = mix(snow_trough, snow_crest, sastrugi);
    col *= 0.95 + 0.08 * detail_n.x;

    mat.albedo = col;
    mat.roughness = mix(0.46, 0.28, sastrugi);
    mat.metallic = 0.0;
    mat.f0 = 0.042;
    mat.cavity = 0.88 + 0.14 * sastrugi;
    mat.emissive = vec3(0.0);
    mat.bump_grad = drift_n.yzw * 0.55 + detail_n.yzw * 0.22 * detail_fade;
    mat.snow_mask = 1.0;
    return mat;
  }

  vec3 vor = triplanar_voronoi(wp, w, 0.85);
  float vein = 1.0 - smoothstep(0.01, 0.18, vor.y);
  float facet = vor.z;
  float pulse = 0.76 + 0.24 * sin(u_time * 2.2 + facet * 6.283 + wp.y * 0.35);

  vec3 cyan_col = vec3(0.08, 0.86, 0.98);
  vec3 violet_col = vec3(0.58, 0.22, 0.98);
  vec3 crystal_col = mix(cyan_col, violet_col, smoothstep(0.28, 0.78, facet));

  mat.albedo = mix(vec3(0.06, 0.12, 0.20), crystal_col * 0.85, 0.45 + 0.55 * vein);
  mat.roughness = mix(0.24, 0.12, vein);
  mat.metallic = 0.15;
  mat.f0 = 0.12;
  mat.cavity = 0.75 + 0.25 * (1.0 - vein);
  mat.emissive = crystal_col * (0.48 + 1.35 * vein) * pulse;
  mat.bump_grad = macro_n.yzw * 0.5 + micro_n.yzw * 1.1;
  mat.snow_mask = 0.0;
  return mat;
}

vec3 ggx_lighting(vec3 n, vec3 v, vec3 l, vec3 albedo, float roughness, float metallic, float f0_scalar, vec3 sun_radiance) {
  float n_dot_l = max(dot(n, l), 0.0);
  if (n_dot_l <= 0.0) return vec3(0.0);
  float n_dot_v = max(dot(n, v), 0.001);
  vec3 h = normalize(l + v);
  float n_dot_h = max(dot(n, h), 0.0);
  float l_dot_h = max(dot(l, h), 0.0);

  float a = max(roughness * roughness, 0.006);
  float a2 = a * a;
  float denom = n_dot_h * n_dot_h * (a2 - 1.0) + 1.0;
  float d_ggx = a2 / (PI * denom * denom + 1e-5);

  float r1 = roughness + 1.0;
  float k = (r1 * r1) / 8.0;
  float g_smith = (n_dot_v / (n_dot_v * (1.0 - k) + k)) * (n_dot_l / (n_dot_l * (1.0 - k) + k));

  vec3 f0 = mix(vec3(f0_scalar), albedo, metallic);
  vec3 fresnel = f0 + (1.0 - f0) * pow(1.0 - l_dot_h, 5.0);

  vec3 spec = (d_ggx * g_smith * fresnel) / (4.0 * n_dot_v * n_dot_l + 1e-4);
  vec3 diff = (1.0 - fresnel) * (1.0 - metallic) * albedo / PI;
  return (diff + spec) * sun_radiance * n_dot_l;
}

void main() {
  vec3 n_geom = normalize(v_normal);
  vec3 sun = normalize(u_sun_dir);
  float sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
  float sun_vis = smoothstep(-0.06, 0.14, sun.y);

  vec3 view_vec = u_camera_pos - v_world_pos;
  float dist = length(view_vec);
  vec3 v = view_vec / max(dist, 1e-4);

  MatSample mat = eval_material(v_biome, v_world_pos, n_geom, dist);
  if (u_lod_tint > 0.5) {
    vec3 lod_col = u_chunk_lod > 0.5 ? vec3(0.96, 0.56, 0.16) : vec3(0.18, 0.85, 0.55);
    mat.albedo = mix(mat.albedo, lod_col, 0.42);
  }

  float bump_fade = 1.0 / (1.0 + dist * 0.022);
  vec3 surf_grad = mat.bump_grad - dot(mat.bump_grad, n_geom) * n_geom;
  vec3 n = normalize(n_geom - surf_grad * 0.24 * bump_fade);

  float ao_pow = pow(clamp(v_ao, 0.0, 1.0), 1.65);
  float amb_ao = mix(0.14, 1.0, ao_pow) * mat.cavity;
  float sun_occ = mix(0.32, 1.0, pow(clamp(v_ao, 0.0, 1.0), 1.35)) * mix(0.55, 1.0, mat.cavity);

  vec3 sun_col = mix(vec3(3.35, 3.05, 2.55), vec3(3.85, 1.65, 0.68), sunset_t) * sun_vis;
  vec3 direct = ggx_lighting(n, v, sun, mat.albedo, mat.roughness, mat.metallic, mat.f0, sun_col) * sun_occ;

  vec3 sky_top = mix(vec3(0.36, 0.56, 0.92), vec3(0.34, 0.26, 0.50), sunset_t);
  vec3 warm_bounce = mix(vec3(0.24, 0.18, 0.12), vec3(0.30, 0.14, 0.08), sunset_t) * (0.4 + 0.6 * sun_vis);
  vec3 sky_hemi = mix(warm_bounce, sky_top, n.y * 0.5 + 0.5);
  vec3 ambient = mat.albedo * sky_hemi * 0.68 * amb_ao;

  vec3 snow_extra = vec3(0.0);
  if (mat.snow_mask > 0.5) {
    float wrap_l = max((dot(n, sun) + 0.35) / 1.35, 0.0);
    snow_extra += vec3(0.38, 0.62, 0.96) * (1.0 - max(dot(n, sun), 0.0)) * wrap_l * 0.28 * amb_ao * sun_vis;
    if (dist < 60.0) {
      vec3 cell = floor(v_world_pos * 13.5);
      vec3 rnd = normalize(hash33(cell) * 2.0 - 1.0);
      vec3 spark_n = normalize(n + rnd * 0.55);
      vec3 h_vec = normalize(sun + v);
      float glints = pow(max(dot(spark_n, h_vec), 0.0), 190.0) * (1.0 - smoothstep(8.0, 60.0, dist));
      snow_extra += sun_col * glints * 1.45 * max(dot(n, sun), 0.0);
    }
  }

  float n_dot_v = max(dot(n, v), 0.0);
  float fresnel_view = mat.f0 + (1.0 - mat.f0) * pow(1.0 - n_dot_v, 5.0);
  vec3 refl_dir = reflect(-v, n);
  vec3 env_sky = mix(warm_bounce, sky_top, clamp(refl_dir.y * 0.5 + 0.5, 0.0, 1.0));
  vec3 env_spec = env_sky * fresnel_view * (1.0 - mat.roughness * 0.78) * amb_ao * 0.65;

  float sun_rim_dir = pow(max(dot(-v, sun), 0.0), 2.5);
  float rim_mask = pow(1.0 - n_dot_v, 3.8) * (0.35 + 0.65 * max(dot(n, sun) * 0.5 + 0.5, 0.0));
  vec3 rim_col = mix(vec3(1.05, 0.92, 0.72), vec3(1.45, 0.62, 0.24), sunset_t);
  vec3 rim_light = rim_col * rim_mask * (0.14 + 0.48 * sun_rim_dir) * sun_vis * amb_ao;

  vec3 crystal_rim = mat.emissive * (0.75 + 0.95 * pow(1.0 - n_dot_v, 2.2));
  vec3 color = direct + ambient + env_spec + rim_light + snow_extra + crystal_rim;

  float fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
  vec3 fog_base = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.32, 0.12), sunset_t);
  float mie_fog = pow(max(dot(-v, sun), 0.0), 4.0) * (0.28 + 0.65 * sunset_t) * sun_vis;
  vec3 fog_color = fog_base + vec3(1.10, 0.48, 0.16) * mie_fog;
  color = mix(color, fog_color, clamp(fog_t, 0.0, 0.94));

  color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
  fragColor = vec4(pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
}
`;

const WATER_VS = `#version 300 es
precision highp float;
uniform mat4 u_view_proj;
uniform vec3 u_camera_pos;
out vec3 v_world_pos;

void main() {
  vec2 quad[6] = vec2[6](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0),
    vec2(-1.0, -1.0), vec2(1.0,  1.0), vec2(-1.0, 1.0)
  );
  vec2 q = quad[gl_VertexID];
  vec3 world = vec3(u_camera_pos.x + q.x * 560.0, -6.0, u_camera_pos.z + q.y * 560.0);
  v_world_pos = world;
  gl_Position = u_view_proj * vec4(world, 1.0);
}
`;

const WATER_FS = `#version 300 es
precision highp float;
in vec3 v_world_pos;
out vec4 fragColor;

uniform vec3 u_camera_pos;
uniform vec3 u_sun_dir;
uniform float u_time;

float w_hash21(vec2 p) {
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

float w_vnoise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(
    mix(w_hash21(i), w_hash21(i + vec2(1.0, 0.0)), u.x),
    mix(w_hash21(i + vec2(0.0, 1.0)), w_hash21(i + vec2(1.0, 1.0)), u.x),
    u.y
  );
}

vec3 eval_sky_linear(vec3 dir, vec3 sun, float sunset_t, float sun_vis, float t) {
  vec3 horizon = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.32, 0.12), sunset_t);
  vec3 zenith  = mix(vec3(0.075, 0.21, 0.56), vec3(0.085, 0.105, 0.29), sunset_t);
  vec3 sky = mix(horizon, zenith, pow(clamp(dir.y, 0.02, 1.0), 0.52));
  float sun_dot = max(dot(dir, sun), 0.0);
  vec3 glow_col = mix(vec3(1.15, 0.90, 0.60), vec3(1.55, 0.52, 0.16), sunset_t);
  sky += glow_col * pow(sun_dot, 8.0) * (0.30 + 0.36 * sunset_t) * sun_vis;
  sky += glow_col * smoothstep(0.996, 0.9996, sun_dot) * 2.4 * sun_vis;

  if (dir.y > 0.02) {
    vec2 uv = dir.xz / max(dir.y + 0.14, 0.05) * 0.48 + vec2(t * 0.018, t * 0.007);
    float c_raw = w_vnoise(uv) * 0.65 + w_vnoise(uv * 2.3 + 4.1) * 0.35;
    float c_mask = smoothstep(0.52, 0.80, c_raw) * smoothstep(0.02, 0.18, dir.y);
    vec3 c_col = mix(vec3(1.18, 1.18, 1.20), vec3(1.45, 0.70, 0.38), sunset_t);
    sky = mix(sky, c_col, c_mask * 0.68);
  }
  return sky;
}

void main() {
  float t = u_time;
  vec2 p = v_world_pos.xz;
  vec3 view_vec = u_camera_pos - v_world_pos;
  float dist = length(view_vec);
  vec3 v = view_vec / max(dist, 1e-4);
  float rf = 1.0 / (1.0 + dist * 0.0085);

  float dhdx = 0.0, dhdz = 0.0, wh = 0.0;
  vec2 dirs[5] = vec2[5](vec2(0.82,0.57), vec2(-0.55,0.83), vec2(0.36,-0.93), vec2(-0.91,-0.41), vec2(0.68,-0.73));
  float freqs[5] = float[5](0.22, 0.37, 0.85, 1.75, 3.20);
  float amps[5]  = float[5](0.24, 0.15, 0.065, 0.032, 0.015);
  float speeds[5] = float[5](1.15, 1.48, 2.15, 2.85, -3.40);
  for (int i = 0; i < 5; i++) {
    float a = amps[i] * rf * (i == 4 ? rf : 1.0);
    float ph = dot(dirs[i], p) * freqs[i] + t * speeds[i];
    wh += a * sin(ph);
    float drv = a * freqs[i] * cos(ph);
    dhdx += dirs[i].x * drv;
    dhdz += dirs[i].y * drv;
  }

  float n_sign = u_camera_pos.y >= -6.0 ? 1.0 : -1.0;
  vec3 n = normalize(vec3(-dhdx, n_sign, -dhdz));
  vec3 sun = normalize(u_sun_dir);
  float sunset_t = 1.0 - smoothstep(0.04, 0.50, clamp(sun.y, 0.0, 1.0));
  float sun_vis = smoothstep(-0.06, 0.14, sun.y);

  float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
  vec3 deep_teal = vec3(0.012, 0.10, 0.16);
  vec3 crest_teal = vec3(0.032, 0.28, 0.34);
  float crest_t = clamp(wh * 1.45 + 0.35 + max(dot(n, sun), 0.0) * 0.35, 0.0, 1.0);
  float sss = pow(max(dot(v, -sun + n * 0.55), 0.0), 3.0) * clamp(wh * 2.4 + 0.35, 0.0, 1.0);
  vec3 sun_tint = mix(vec3(1.0, 0.96, 0.90), vec3(1.18, 0.68, 0.38), sunset_t);
  vec3 body = mix(deep_teal, crest_teal, crest_t) * (0.42 + 0.58 * sun_vis) * sun_tint;
  body += vec3(0.05, 0.38, 0.40) * sss * sun_vis;

  vec3 sky_refl = eval_sky_linear(reflect(-v, n), sun, sunset_t, sun_vis, t);
  vec3 color = mix(body, sky_refl, clamp(fresnel * 1.06, 0.04, 0.96));

  float foam_n = w_vnoise(p * 0.65 + vec2(t * 0.35, -t * 0.22));
  float crest_foam = smoothstep(0.22, 0.40, wh) * smoothstep(0.54, 0.80, foam_n) * rf;
  vec3 foam_col = mix(vec3(0.88, 0.94, 0.98), vec3(1.05, 0.75, 0.52), sunset_t) * (0.4 + 0.6 * sun_vis);
  color = mix(color, foam_col, crest_foam * 0.45);

  vec3 half_vec = normalize(sun + v);
  float n_dot_h = max(dot(n, half_vec), 0.0);
  vec3 sparkle = mix(vec3(1.45, 1.32, 1.10), vec3(1.85, 0.90, 0.38), sunset_t);
  color += sparkle * (pow(n_dot_h, 48.0) * 0.22 + pow(n_dot_h, 140.0) * 0.72 + pow(n_dot_h, 640.0) * 4.2) * sun_vis;

  float fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
  vec3 fog_base = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.32, 0.12), sunset_t);
  float mie_fog = pow(max(dot(-v, sun), 0.0), 4.0) * (0.28 + 0.65 * sunset_t) * sun_vis;
  vec3 fog_color = fog_base + vec3(1.10, 0.48, 0.16) * mie_fog;
  color = mix(color, fog_color, clamp(fog_t, 0.0, 0.94));

  color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
  color = pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2));
  float alpha = mix(mix(0.62, 0.95, fresnel), 0.98, clamp(fog_t, 0.0, 1.0));
  fragColor = vec4(color, alpha);
}
`;

function compileProgram(gl: WebGL2RenderingContext, vsSrc: string, fsSrc: string): WebGLProgram {
  const compile = (type: number, src: string) => {
    const sh = gl.createShader(type)!;
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) {
      throw new Error(gl.getShaderInfoLog(sh) || "WebGL2 shader compile failed");
    }
    return sh;
  };
  const prog = gl.createProgram()!;
  gl.attachShader(prog, compile(gl.VERTEX_SHADER, vsSrc));
  gl.attachShader(prog, compile(gl.FRAGMENT_SHADER, fsSrc));
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
    throw new Error(gl.getProgramInfoLog(prog) || "WebGL2 program link failed");
  }
  return prog;
}

export class WebGL2WorldRenderer {
  private gl!: WebGL2RenderingContext;
  private skyProg!: WebGLProgram;
  private terrainProg!: WebGLProgram;
  private waterProg!: WebGLProgram;
  private emptyVao!: WebGLVertexArrayObject;
  private chunks = new Map<string, GlChunkEntry>();
  private totalTriangles = 0;
  public lodTint = false;
  public sunDir: Vec3 = [0.55, 0.72, 0.42];
  public readonly backendName = "WebGL2 fallback";

  constructor(private canvas: HTMLCanvasElement) {}

  async init(): Promise<void> {
    const gl = this.canvas.getContext("webgl2", { antialias: true, alpha: false });
    if (!gl) {
      throw new Error("Neither WebGPU nor WebGL2 is available in this browser.");
    }
    this.gl = gl;
    this.skyProg = compileProgram(gl, SKY_VS, SKY_FS);
    this.terrainProg = compileProgram(gl, TERRAIN_VS, TERRAIN_FS);
    this.waterProg = compileProgram(gl, WATER_VS, WATER_FS);
    this.emptyVao = gl.createVertexArray()!;
    this.resize();
  }

  resize(): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.floor(window.innerWidth * dpr));
    const h = Math.max(1, Math.floor(window.innerHeight * dpr));
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.gl.viewport(0, 0, w, h);
  }

  upsertChunk(chunk: DecodedChunk): void {
    const key = chunkKey(chunk.coord);
    this.deleteChunkByKey(key);
    const gl = this.gl;

    let vao: WebGLVertexArrayObject | null = null;
    let vbo: WebGLBuffer | null = null;
    let ibo: WebGLBuffer | null = null;
    if (chunk.vertexCount > 0 && chunk.indexCount > 0) {
      vao = gl.createVertexArray()!;
      gl.bindVertexArray(vao);

      vbo = gl.createBuffer()!;
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferData(gl.ARRAY_BUFFER, chunk.vertexBytes, gl.STATIC_DRAW);

      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 32, 0);
      gl.enableVertexAttribArray(1);
      gl.vertexAttribPointer(1, 1, gl.FLOAT, false, 32, 12);
      gl.enableVertexAttribArray(2);
      gl.vertexAttribPointer(2, 3, gl.FLOAT, false, 32, 16);
      gl.enableVertexAttribArray(3);
      gl.vertexAttribPointer(3, 1, gl.FLOAT, false, 32, 28);

      ibo = gl.createBuffer()!;
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ibo);
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, chunk.indexBytes, gl.STATIC_DRAW);
      gl.bindVertexArray(null);
    }

    const triangleCount = Math.floor(chunk.indexCount / 3);
    this.totalTriangles += triangleCount;
    this.chunks.set(key, {
      vao,
      vbo,
      ibo,
      indexCount: chunk.indexCount,
      triangleCount,
      lod: chunk.coord.lod,
    });
  }

  evictChunks(coords: ChunkCoord[]): void {
    for (const c of coords) this.deleteChunkByKey(chunkKey(c));
  }

  clearAllChunks(): void {
    for (const key of Array.from(this.chunks.keys())) this.deleteChunkByKey(key);
  }

  private deleteChunkByKey(key: string): void {
    const e = this.chunks.get(key);
    if (!e) return;
    if (e.vao) this.gl.deleteVertexArray(e.vao);
    if (e.vbo) this.gl.deleteBuffer(e.vbo);
    if (e.ibo) this.gl.deleteBuffer(e.ibo);
    this.totalTriangles -= e.triangleCount;
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
    const gl = this.gl;
    const aspect = this.getAspect();
    const proj = perspectiveNO((62 * Math.PI) / 180, aspect, 0.3, 600.0);
    const view = lookAt(eye, [eye[0] + dir[0], eye[1] + dir[1], eye[2] + dir[2]], [0, 1, 0]);
    const viewProj = multiplyMat4(proj, view);
    const invViewProj = invertMat4(viewProj);

    const sunLen = Math.hypot(this.sunDir[0], this.sunDir[1], this.sunDir[2]) || 1;
    const sx = this.sunDir[0] / sunLen;
    const sy = this.sunDir[1] / sunLen;
    const sz = this.sunDir[2] / sunLen;

    gl.clearColor(0.08, 0.14, 0.24, 1.0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    // 1. Sky
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.BLEND);
    gl.disable(gl.CULL_FACE);
    gl.useProgram(this.skyProg);
    gl.uniformMatrix4fv(gl.getUniformLocation(this.skyProg, "u_inv_view_proj"), false, invViewProj);
    gl.uniform3f(gl.getUniformLocation(this.skyProg, "u_camera_pos"), eye[0], eye[1], eye[2]);
    gl.uniform3f(gl.getUniformLocation(this.skyProg, "u_sun_dir"), sx, sy, sz);
    gl.uniform1f(gl.getUniformLocation(this.skyProg, "u_time"), timeSec);
    gl.bindVertexArray(this.emptyVao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    // 2. Terrain
    gl.enable(gl.DEPTH_TEST);
    gl.depthMask(true);
    gl.enable(gl.CULL_FACE);
    gl.cullFace(gl.BACK);
    gl.frontFace(gl.CCW);
    gl.useProgram(this.terrainProg);
    gl.uniformMatrix4fv(gl.getUniformLocation(this.terrainProg, "u_view_proj"), false, viewProj);
    gl.uniform3f(gl.getUniformLocation(this.terrainProg, "u_camera_pos"), eye[0], eye[1], eye[2]);
    gl.uniform3f(gl.getUniformLocation(this.terrainProg, "u_sun_dir"), sx, sy, sz);
    gl.uniform1f(gl.getUniformLocation(this.terrainProg, "u_lod_tint"), this.lodTint ? 1.0 : 0.0);
    gl.uniform1f(gl.getUniformLocation(this.terrainProg, "u_time"), timeSec);
    const lodLoc = gl.getUniformLocation(this.terrainProg, "u_chunk_lod");

    for (const chunk of this.chunks.values()) {
      if (!chunk.vao || chunk.indexCount === 0) continue;
      gl.uniform1f(lodLoc, chunk.lod);
      gl.bindVertexArray(chunk.vao);
      gl.drawElements(gl.TRIANGLES, chunk.indexCount, gl.UNSIGNED_INT, 0);
    }

    // 3. Water
    gl.disable(gl.CULL_FACE);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    gl.depthMask(false);
    gl.useProgram(this.waterProg);
    gl.uniformMatrix4fv(gl.getUniformLocation(this.waterProg, "u_view_proj"), false, viewProj);
    gl.uniform3f(gl.getUniformLocation(this.waterProg, "u_camera_pos"), eye[0], eye[1], eye[2]);
    gl.uniform3f(gl.getUniformLocation(this.waterProg, "u_sun_dir"), sx, sy, sz);
    gl.uniform1f(gl.getUniformLocation(this.waterProg, "u_time"), timeSec);
    gl.bindVertexArray(this.emptyVao);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
    gl.depthMask(true);
    gl.disable(gl.BLEND);
  }
}
