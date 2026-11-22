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

@fragment
fn fs_sky(in: SkyOut) -> @location(0) vec4<f32> {
    let clip = vec4<f32>(in.ndc, 1.0, 1.0);
    let world_h = frame.inv_view_proj * clip;
    let dir = normalize(world_h.xyz / world_h.w - frame.camera_pos.xyz);

    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));

    let horizon = mix(vec3<f32>(0.76, 0.86, 0.96), vec3<f32>(0.96, 0.56, 0.30), sunset_t);
    let zenith = mix(vec3<f32>(0.14, 0.34, 0.68), vec3<f32>(0.14, 0.18, 0.42), sunset_t);
    let ground = mix(vec3<f32>(0.15, 0.19, 0.27), vec3<f32>(0.24, 0.14, 0.14), sunset_t);

    var sky = select(
        mix(ground, horizon, clamp(dir.y * 3.5 + 1.0, 0.0, 1.0)),
        mix(horizon, zenith, pow(max(dir.y, 0.0), 0.58)),
        dir.y >= 0.0,
    );

    let sun_dot = max(dot(dir, sun), 0.0);
    let glow_col = mix(vec3<f32>(1.0, 0.85, 0.62), vec3<f32>(1.0, 0.52, 0.18), sunset_t);
    let disc_col = mix(vec3<f32>(1.0, 0.96, 0.88), vec3<f32>(1.0, 0.80, 0.44), sunset_t);

    // Warm horizon Mie scatter toward the sun during golden hour
    let horizon_band = exp(-abs(dir.y) * 5.5);
    sky += vec3<f32>(0.98, 0.46, 0.16) * pow(sun_dot, 3.0) * horizon_band * sunset_t * 0.55;
    sky += glow_col * pow(sun_dot, 12.0) * (0.35 + 0.28 * sunset_t);
    sky += disc_col * smoothstep(0.9985, 0.9996, sun_dot) * 1.4;

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

fn biome_albedo(biome: f32, world_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    let strata = 0.9 + 0.1 * sin(world_pos.y * 0.7 + sin(world_pos.x * 0.12) * 1.5);
    let id = i32(round(biome));
    if (id == 0) {
        // Cavern basalt
        return vec3<f32>(0.18, 0.20, 0.25) * strata;
    }
    if (id == 1) {
        // Warm sandstone
        return vec3<f32>(0.76, 0.67, 0.49) * strata;
    }
    if (id == 2) {
        // Lush alpine meadow
        let tint = 0.88 + 0.16 * sin(world_pos.x * 0.05 + world_pos.z * 0.04);
        return vec3<f32>(0.24, 0.54, 0.26) * tint;
    }
    if (id == 3) {
        // Cliff granite rock
        return vec3<f32>(0.42, 0.43, 0.46) * strata;
    }
    if (id == 4) {
        // Crisp snow
        return vec3<f32>(0.90, 0.94, 0.98);
    }
    // id == 5: Bioluminescent cavern crystal
    return vec3<f32>(0.14, 0.88, 0.95);
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let n = normalize(in.normal);
    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));
    let view_vec = frame.camera_pos.xyz - in.world_pos;
    let dist = length(view_vec);
    let v = view_vec / max(dist, 1e-4);

    var albedo = biome_albedo(in.biome, in.world_pos, n);

    // Optional LOD visualization tint
    if (frame.params.y > 0.5) {
        let lod_color = select(vec3<f32>(0.18, 0.85, 0.55), vec3<f32>(0.96, 0.56, 0.16), chunk.lod_info.x > 0.5);
        albedo = mix(albedo, lod_color, 0.42);
    }

    let n_dot_l = max(dot(n, sun), 0.0);
    let half_vec = normalize(sun + v);
    let spec_tint = mix(vec3<f32>(1.0, 0.98, 0.92), vec3<f32>(1.0, 0.65, 0.34), sunset_t);
    let spec = pow(max(dot(n, half_vec), 0.0), 32.0) * 0.16 * step(0.1, n_dot_l);

    let sky_bot = mix(vec3<f32>(0.16, 0.17, 0.22), vec3<f32>(0.22, 0.14, 0.16), sunset_t);
    let sky_top = mix(vec3<f32>(0.48, 0.64, 0.88), vec3<f32>(0.38, 0.32, 0.52), sunset_t);
    let sky_hemi = mix(sky_bot, sky_top, n.y * 0.5 + 0.5);

    let sun_col = mix(vec3<f32>(1.08, 0.98, 0.84), vec3<f32>(1.38, 0.64, 0.26), sunset_t);
    let sun_light = sun_col * n_dot_l;

    // Subtle cavern glow when deep underground
    let emissive = select(vec3<f32>(0.0), vec3<f32>(0.12, 0.85, 0.95) * 0.85, i32(round(in.biome)) == 5);

    var color = albedo * (sun_light + sky_hemi * 0.52) * in.ao + spec_tint * spec + emissive;

    // Atmospheric aerial perspective fog
    let fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
    let fog_color = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.30, 0.11), sunset_t);
    color = mix(color, fog_color, clamp(fog_t, 0.0, 0.92));

    // Simple ACES filmic approximation
    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
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

fn eval_sky_linear(dir: vec3<f32>, sun: vec3<f32>, sunset_t: f32) -> vec3<f32> {
    let horizon = mix(vec3<f32>(0.68, 0.80, 0.94), vec3<f32>(0.96, 0.34, 0.13), sunset_t);
    let zenith = mix(vec3<f32>(0.08, 0.22, 0.56), vec3<f32>(0.09, 0.11, 0.28), sunset_t);
    let y = clamp(dir.y, 0.02, 1.0);
    var sky = mix(horizon, zenith, pow(y, 0.55));
    let sun_dot = max(dot(dir, sun), 0.0);
    let glow_col = mix(vec3<f32>(1.15, 0.88, 0.56), vec3<f32>(1.45, 0.52, 0.16), sunset_t);
    sky += glow_col * pow(sun_dot, 10.0) * (0.42 + 0.35 * sunset_t);
    sky += glow_col * smoothstep(0.996, 0.9996, sun_dot) * 2.2;
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
        let amp = 0.014 * ripple_fade * ripple_fade;
        let phase = dot(d, p) * freq - t * 3.40;
        let deriv = amp * freq * cos(phase);
        dhdx += d.x * deriv;
        dhdz += d.y * deriv;
    }

    let n_sign = select(-1.0, 1.0, frame.camera_pos.y >= WATER_LEVEL);
    let n = normalize(vec3<f32>(-dhdx, n_sign, -dhdz));

    let sun = normalize(frame.sun_dir.xyz);
    let sunset_t = 1.0 - smoothstep(0.05, 0.48, clamp(sun.y, 0.0, 1.0));
    let sun_vis = smoothstep(-0.04, 0.14, sun.y);

    // Schlick Fresnel (F0 = 0.02 for water)
    let n_dot_v = max(dot(n, v), 0.0);
    let fresnel = 0.02 + 0.98 * pow(1.0 - n_dot_v, 5.0);

    // Deep teal body color with subtle wave-crest turquoise scatter
    let deep_teal = vec3<f32>(0.012, 0.11, 0.16);
    let crest_teal = vec3<f32>(0.028, 0.25, 0.30);
    let sun_diffuse = max(dot(n, sun), 0.0);
    let crest_t = clamp(wave_h * 1.4 + 0.35 + sun_diffuse * 0.35, 0.0, 1.0);
    let sun_tint = mix(vec3<f32>(1.0, 0.96, 0.90), vec3<f32>(1.15, 0.68, 0.38), sunset_t);
    let water_body = mix(deep_teal, crest_teal, crest_t) * (0.45 + 0.55 * sun_vis) * sun_tint;

    // Sky reflection
    let refl_dir = reflect(-v, n);
    let sky_refl = eval_sky_linear(refl_dir, sun, sunset_t);

    var color = mix(water_body, sky_refl, clamp(fresnel * 1.08, 0.05, 0.95));

    // Crisp sun specular sparkle + soft sun glitter path
    let half_vec = normalize(sun + v);
    let n_dot_h = max(dot(n, half_vec), 0.0);
    let sparkle_col = mix(vec3<f32>(1.35, 1.24, 1.05), vec3<f32>(1.65, 0.85, 0.38), sunset_t);
    let spec_glint = pow(n_dot_h, 96.0) * 0.48 + pow(n_dot_h, 520.0) * 3.2;
    color += sparkle_col * spec_glint * sun_vis;

    // Distance fog matching terrain exactly
    let fog_t = 1.0 - exp(-pow(dist / 260.0, 2.2));
    let fog_color = mix(vec3<f32>(0.68, 0.80, 0.93), vec3<f32>(0.96, 0.30, 0.11), sunset_t);
    color = mix(color, fog_color, clamp(fog_t, 0.0, 0.92));

    // ACES filmic tone mapping + gamma matching terrain
    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
    color = pow(clamp(color, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(1.0 / 2.2));

    let alpha = mix(mix(0.74, 0.95, fresnel), 0.98, clamp(fog_t, 0.0, 1.0));
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
