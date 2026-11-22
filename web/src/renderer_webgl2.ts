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

void main() {
  vec4 world_h = u_inv_view_proj * vec4(v_ndc, 1.0, 1.0);
  vec3 dir = normalize(world_h.xyz / world_h.w - u_camera_pos);
  vec3 sun = normalize(u_sun_dir);
  float sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));

  vec3 horizon = mix(vec3(0.76, 0.86, 0.96), vec3(0.96, 0.56, 0.30), sunset_t);
  vec3 zenith  = mix(vec3(0.14, 0.34, 0.68), vec3(0.14, 0.18, 0.42), sunset_t);
  vec3 ground  = mix(vec3(0.15, 0.19, 0.27), vec3(0.24, 0.14, 0.14), sunset_t);

  vec3 sky = dir.y >= 0.0
    ? mix(horizon, zenith, pow(max(dir.y, 0.0), 0.58))
    : mix(ground, horizon, clamp(dir.y * 3.5 + 1.0, 0.0, 1.0));

  float sun_dot = max(dot(dir, sun), 0.0);
  vec3 glow_col = mix(vec3(1.0, 0.85, 0.62), vec3(1.0, 0.52, 0.18), sunset_t);
  vec3 disc_col = mix(vec3(1.0, 0.96, 0.88), vec3(1.0, 0.80, 0.44), sunset_t);

  float horizon_band = exp(-abs(dir.y) * 5.5);
  sky += vec3(0.98, 0.46, 0.16) * pow(sun_dot, 3.0) * horizon_band * sunset_t * 0.55;
  sky += glow_col * pow(sun_dot, 12.0) * (0.35 + 0.28 * sunset_t);
  sky += disc_col * smoothstep(0.9985, 0.9996, sun_dot) * 1.4;
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

vec3 biome_albedo(float biome, vec3 wp) {
  float strata = 0.9 + 0.1 * sin(wp.y * 0.7 + sin(wp.x * 0.12) * 1.5);
  int id = int(floor(biome + 0.5));
  if (id == 0) return vec3(0.18, 0.20, 0.25) * strata;
  if (id == 1) return vec3(0.76, 0.67, 0.49) * strata;
  if (id == 2) return vec3(0.24, 0.54, 0.26) * (0.88 + 0.16 * sin(wp.x * 0.05 + wp.z * 0.04));
  if (id == 3) return vec3(0.42, 0.43, 0.46) * strata;
  if (id == 4) return vec3(0.90, 0.94, 0.98);
  return vec3(0.14, 0.88, 0.95);
}

void main() {
  vec3 n = normalize(v_normal);
  vec3 sun = normalize(u_sun_dir);
  float sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));
  vec3 view_vec = u_camera_pos - v_world_pos;
  float dist = length(view_vec);
  vec3 v = view_vec / max(dist, 1e-4);

  vec3 albedo = biome_albedo(v_biome, v_world_pos);
  if (u_lod_tint > 0.5) {
    vec3 lod_col = u_chunk_lod > 0.5 ? vec3(0.96, 0.56, 0.16) : vec3(0.18, 0.85, 0.55);
    albedo = mix(albedo, lod_col, 0.42);
  }

  float n_dot_l = max(dot(n, sun), 0.0);
  vec3 half_vec = normalize(sun + v);
  vec3 spec_tint = mix(vec3(1.0, 0.98, 0.92), vec3(1.0, 0.65, 0.34), sunset_t);
  float spec = pow(max(dot(n, half_vec), 0.0), 32.0) * 0.16 * step(0.1, n_dot_l);

  vec3 sky_bot = mix(vec3(0.16, 0.17, 0.22), vec3(0.22, 0.14, 0.16), sunset_t);
  vec3 sky_top = mix(vec3(0.48, 0.64, 0.88), vec3(0.38, 0.32, 0.52), sunset_t);
  vec3 sky_hemi = mix(sky_bot, sky_top, n.y * 0.5 + 0.5);
  vec3 sun_col = mix(vec3(1.08, 0.98, 0.84), vec3(1.38, 0.64, 0.26), sunset_t);
  vec3 emissive = int(floor(v_biome + 0.5)) == 5 ? vec3(0.12, 0.85, 0.95) * 0.85 : vec3(0.0);

  vec3 color = albedo * (sun_col * n_dot_l + sky_hemi * 0.52) * v_ao + spec_tint * spec + emissive;
  float fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
  vec3 fog_color = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.30, 0.11), sunset_t);
  color = mix(color, fog_color, clamp(fog_t, 0.0, 0.92));

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

vec3 eval_sky_linear(vec3 dir, vec3 sun, float sunset_t) {
  vec3 horizon = mix(vec3(0.68, 0.80, 0.94), vec3(0.96, 0.34, 0.13), sunset_t);
  vec3 zenith  = mix(vec3(0.08, 0.22, 0.56), vec3(0.09, 0.11, 0.28), sunset_t);
  vec3 sky = mix(horizon, zenith, pow(clamp(dir.y, 0.02, 1.0), 0.55));
  float sun_dot = max(dot(dir, sun), 0.0);
  vec3 glow_col = mix(vec3(1.15, 0.88, 0.56), vec3(1.45, 0.52, 0.16), sunset_t);
  sky += glow_col * pow(sun_dot, 10.0) * (0.42 + 0.35 * sunset_t);
  sky += glow_col * smoothstep(0.996, 0.9996, sun_dot) * 2.2;
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
  float amps[5]  = float[5](0.24, 0.15, 0.065, 0.032, 0.014);
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
  float sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));
  float sun_vis = smoothstep(-0.04, 0.14, sun.y);

  float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
  vec3 deep_teal = vec3(0.012, 0.11, 0.16);
  vec3 crest_teal = vec3(0.028, 0.25, 0.30);
  float crest_t = clamp(wh * 1.4 + 0.35 + max(dot(n, sun), 0.0) * 0.35, 0.0, 1.0);
  vec3 sun_tint = mix(vec3(1.0, 0.96, 0.90), vec3(1.15, 0.68, 0.38), sunset_t);
  vec3 body = mix(deep_teal, crest_teal, crest_t) * (0.45 + 0.55 * sun_vis) * sun_tint;

  vec3 sky_refl = eval_sky_linear(reflect(-v, n), sun, sunset_t);
  vec3 color = mix(body, sky_refl, clamp(fresnel * 1.08, 0.05, 0.95));

  vec3 half_vec = normalize(sun + v);
  float n_dot_h = max(dot(n, half_vec), 0.0);
  vec3 sparkle = mix(vec3(1.35, 1.24, 1.05), vec3(1.65, 0.85, 0.38), sunset_t);
  color += sparkle * (pow(n_dot_h, 96.0) * 0.48 + pow(n_dot_h, 520.0) * 3.2) * sun_vis;

  float fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
  vec3 fog_color = mix(vec3(0.68, 0.80, 0.93), vec3(0.96, 0.30, 0.11), sunset_t);
  color = mix(color, fog_color, clamp(fog_t, 0.0, 0.92));

  color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
  color = pow(clamp(color, 0.0, 1.0), vec3(1.0 / 2.2));
  float alpha = mix(mix(0.74, 0.95, fresnel), 0.98, clamp(fog_t, 0.0, 1.0));
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
