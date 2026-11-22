using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill
{
    /// <summary>
    /// High-performance GPU-instanced procedural foliage & prop scatterer for Unity 6 HDRP.
    /// Listens to WorldMillStreamer chunk events and populates dense Nordic pine forests,
    /// meadow grass tufts, mossy boulders, and glowing cavern crystal spires at 120+ FPS.
    /// </summary>
    public sealed class WorldMillFoliageSystem : MonoBehaviour
    {
        public WorldMillStreamer streamer;
        public float waterLevel = -6.0f;

        [Header("Forest & Meadow Density")]
        [Range(0, 40)] public int maxTreesPerChunk = 14;
        [Range(3f, 14f)] public float minTreeSpacing = 6.8f;
        [Range(0.2f, 0.85f)] public float forestThreshold = 0.55f;
        [Range(0, 200)] public int maxGrassPerChunk = 110;

        public int TotalTrees => _allTrunks.Count;
        public int TotalGrass => _allGrass.Count;
        public int TotalBoulders => _allBoulders.Count;
        public int TotalCrystals => _allCrystals.Count;

        private sealed class ChunkInstances
        {
            public readonly List<Matrix4x4> Trunks = new();
            public readonly List<Matrix4x4> Canopies = new();
            public readonly List<Matrix4x4> Grass = new();
            public readonly List<Matrix4x4> Boulders = new();
            public readonly List<Matrix4x4> Crystals = new();
        }

        private readonly Dictionary<ChunkCoord, ChunkInstances> _perChunk = new();
        private readonly List<Matrix4x4> _allTrunks = new(4096);
        private readonly List<Matrix4x4> _allCanopies = new(4096);
        private readonly List<Matrix4x4> _allGrass = new(16384);
        private readonly List<Matrix4x4> _allBoulders = new(2048);
        private readonly List<Matrix4x4> _allCrystals = new(1024);
        private readonly Matrix4x4[] _batchBuf = new Matrix4x4[1023];
        private bool _dirty;

        private Mesh _trunkMesh;
        private Mesh _canopyMesh;
        private Mesh _grassMesh;
        private Mesh _boulderMesh;
        private Mesh _crystalMesh;

        private Material _trunkMat;
        private Material _canopyMat;
        private Material _grassMat;
        private Material _boulderMat;
        private Material _crystalMat;

        private void Awake()
        {
            BuildProceduralMeshes();
            BuildHdrpMaterials();
        }

        private void OnEnable()
        {
            if (streamer != null)
            {
                streamer.OnChunkSpawned += HandleChunkSpawned;
                streamer.OnChunkRemoved += HandleChunkRemoved;
                streamer.OnWorldCleared += HandleWorldCleared;
            }
        }

        private void OnDisable()
        {
            if (streamer != null)
            {
                streamer.OnChunkSpawned -= HandleChunkSpawned;
                streamer.OnChunkRemoved -= HandleChunkRemoved;
                streamer.OnWorldCleared -= HandleWorldCleared;
            }
        }

        private void OnDestroy()
        {
            if (_trunkMesh) Destroy(_trunkMesh);
            if (_canopyMesh) Destroy(_canopyMesh);
            if (_grassMesh) Destroy(_grassMesh);
            if (_boulderMesh) Destroy(_boulderMesh);
            if (_crystalMesh) Destroy(_crystalMesh);
            if (_trunkMat) Destroy(_trunkMat);
            if (_canopyMat) Destroy(_canopyMat);
            if (_grassMat) Destroy(_grassMat);
            if (_boulderMat) Destroy(_boulderMat);
            if (_crystalMat) Destroy(_crystalMat);
        }

        private void HandleChunkSpawned(ChunkCoord coord, DecodedChunk chunk)
        {
            // Scatter rich foliage & props on high-detail LOD 0 chunks only
            if (coord.Lod != 0 || chunk.IsEmpty) return;

            var inst = new ChunkInstances();
            var placedTreePositions = new List<Vector3>(maxTreesPerChunk);
            float minSpacingSq = minTreeSpacing * minTreeSpacing;
            var pos = chunk.Positions;
            var norm = chunk.Normals;
            var col = chunk.LayerColors;
            var idx = chunk.Indices;
            uint seed = chunk.Seed;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                Vector3 p = (pos[i0] + pos[i1] + pos[i2]) * (1f / 3f);
                Vector3 n = (norm[i0] + norm[i1] + norm[i2]).normalized;
                Color c = (col[i0] + col[i1] + col[i2]) * 0.3333f;

                uint h = HashPos(p, seed);
                float r0 = (h & 0xFFFFu) / 65535f;
                float r1 = ((h >> 16) & 0xFFFFu) / 65535f;
                float yaw = r1 * 360f;

                // 1. Scenic spaced Nordic Pine groves + wide open sunlit meadows
                if (p.y > waterLevel + 2.0f && p.y < 28f && n.y > 0.78f && c.r > 0.52f)
                {
                    // Coherent grove mask: open meadows + natural forest pockets
                    float grove = Mathf.PerlinNoise(p.x * 0.022f + seed * 0.13f, p.z * 0.022f + seed * 0.27f);
                    float treeChance = grove > forestThreshold ? 0.035f : 0.003f;
                    if (r0 < treeChance && inst.Trunks.Count < maxTreesPerChunk)
                    {
                        bool tooClose = false;
                        for (int k = 0; k < placedTreePositions.Count; k++)
                        {
                            if ((placedTreePositions[k] - p).sqrMagnitude < minSpacingSq)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                        if (!tooClose)
                        {
                            placedTreePositions.Add(p);
                            float s = Mathf.Lerp(0.85f, 1.55f, r1);
                            float hScale = s * Mathf.Lerp(0.95f, 1.30f, ((h >> 8) & 0xFFu) / 255f);
                            var tilt = Quaternion.Euler((r0 - 0.015f) * 28f, yaw, (r1 - 0.5f) * 4f);
                            var mat = Matrix4x4.TRS(p - new Vector3(0f, 0.25f, 0f), tilt, new Vector3(s, hScale, s));
                            inst.Trunks.Add(mat);
                            inst.Canopies.Add(mat);
                            continue;
                        }
                    }

                    // Meadow grass tufts
                    if (r0 < 0.22f && inst.Grass.Count < maxGrassPerChunk)
                    {
                        float gs = Mathf.Lerp(0.70f, 1.30f, r1);
                        var align = Quaternion.FromToRotation(Vector3.up, n) * Quaternion.Euler(0f, yaw, 0f);
                        inst.Grass.Add(Matrix4x4.TRS(p - new Vector3(0f, 0.06f, 0f), align, new Vector3(gs, gs * 1.1f, gs)));
                    }
                }
                // 2. Mossy boulders on rocky slopes & riverbanks
                else if (p.y > waterLevel - 0.5f && n.y > 0.52f && (c.g > 0.35f || c.r < 0.25f))
                {
                    if (r0 < 0.022f && inst.Boulders.Count < 28)
                    {
                        float bs = Mathf.Lerp(0.45f, 1.55f, r1);
                        var rot = Quaternion.Euler(r0 * 180f, yaw, r1 * 120f);
                        inst.Boulders.Add(Matrix4x4.TRS(p - new Vector3(0f, bs * 0.25f, 0f), rot, new Vector3(bs * 1.25f, bs * 0.8f, bs)));
                    }
                }
                // 3. Bioluminescent Crystal Spires inside deep subterranean caverns!
                else if (p.y < waterLevel - 5.0f && n.y > 0.35f)
                {
                    float vein = Mathf.PerlinNoise(p.x * 0.09f + 17.3f, p.z * 0.09f + 9.1f);
                    if (vein > 0.62f && r0 < 0.06f && inst.Crystals.Count < 24)
                    {
                        float cs = Mathf.Lerp(0.6f, 1.8f, r1);
                        var align = Quaternion.FromToRotation(Vector3.up, n) * Quaternion.Euler(0f, yaw, 0f);
                        inst.Crystals.Add(Matrix4x4.TRS(p - n * 0.12f, align, new Vector3(cs * 0.7f, cs * 1.5f, cs * 0.7f)));
                    }
                }
            }

            _perChunk[coord] = inst;
            _dirty = true;
        }

        private void HandleChunkRemoved(ChunkCoord coord)
        {
            if (_perChunk.Remove(coord))
                _dirty = true;
        }

        private void HandleWorldCleared()
        {
            _perChunk.Clear();
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (_dirty)
            {
                RebuildFlatLists();
                _dirty = false;
            }

            DrawInstanced(_trunkMesh, _trunkMat, _allTrunks, ShadowCastingMode.On);
            DrawInstanced(_canopyMesh, _canopyMat, _allCanopies, ShadowCastingMode.On);
            DrawInstanced(_boulderMesh, _boulderMat, _allBoulders, ShadowCastingMode.On);
            DrawInstanced(_crystalMesh, _crystalMat, _allCrystals, ShadowCastingMode.Off);
            DrawInstanced(_grassMesh, _grassMat, _allGrass, ShadowCastingMode.Off);
        }

        private void RebuildFlatLists()
        {
            _allTrunks.Clear();
            _allCanopies.Clear();
            _allGrass.Clear();
            _allBoulders.Clear();
            _allCrystals.Clear();

            foreach (var c in _perChunk.Values)
            {
                _allTrunks.AddRange(c.Trunks);
                _allCanopies.AddRange(c.Canopies);
                _allGrass.AddRange(c.Grass);
                _allBoulders.AddRange(c.Boulders);
                _allCrystals.AddRange(c.Crystals);
            }
        }

        private void DrawInstanced(Mesh mesh, Material mat, List<Matrix4x4> list, ShadowCastingMode shadows)
        {
            if (mesh == null || mat == null || list.Count == 0) return;
            var rp = new RenderParams(mat)
            {
                shadowCastingMode = shadows,
                receiveShadows = true,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 2000f),
            };
            int total = list.Count;
            for (int off = 0; off < total; off += 1023)
            {
                int count = Mathf.Min(1023, total - off);
                list.CopyTo(off, _batchBuf, 0, count);
                Graphics.RenderMeshInstanced(rp, mesh, 0, _batchBuf, count);
            }
        }

        private static uint HashPos(Vector3 p, uint seed)
        {
            int qx = Mathf.FloorToInt(p.x * 2f);
            int qy = Mathf.FloorToInt(p.y * 2f);
            int qz = Mathf.FloorToInt(p.z * 2f);
            uint h = (uint)(qx * 374761393 + qy * 668265263 + qz * 2147483647) ^ (seed * 1597334677u);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }

        private void BuildHdrpMaterials()
        {
            var lit = Shader.Find("HDRP/Lit");
            if (lit == null) return;

            _trunkMat = MakeLit(lit, new Color(0.24f, 0.15f, 0.09f), 0.12f, 0f, Color.black, false);
            _canopyMat = MakeLit(lit, new Color(0.08f, 0.24f, 0.10f), 0.16f, 0f, Color.black, true);
            _grassMat = MakeLit(lit, new Color(0.18f, 0.42f, 0.13f), 0.18f, 0f, Color.black, true);
            _boulderMat = MakeLit(lit, new Color(0.32f, 0.33f, 0.35f), 0.26f, 0.04f, Color.black, false);
            _crystalMat = MakeLit(lit, new Color(0.12f, 0.85f, 0.96f), 0.92f, 0.2f, new Color(0.25f, 2.6f, 3.4f), false);
        }

        private static Material MakeLit(Shader lit, Color baseCol, float smoothness, float metallic, Color emissive, bool doubleSided)
        {
            var m = new Material(lit) { enableInstancing = true };
            m.SetColor("_BaseColor", baseCol);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (doubleSided)
                m.SetFloat("_DoubleSidedEnable", 1f);
            if (emissive.maxColorComponent > 0.01f)
            {
                m.SetColor("_EmissiveColor", emissive);
                m.SetColor("_EmissiveColorLDR", emissive);
                m.SetFloat("_UseEmissiveIntensity", 1f);
                m.SetFloat("_EmissiveIntensity", 3.5f);
            }
            HDMaterial.ValidateMaterial(m);
            return m;
        }

        private void BuildProceduralMeshes()
        {
            _trunkMesh = CreateCylinderMesh(0.26f, 0.10f, 3.6f, 7);
            _canopyMesh = CreatePineCanopyMesh();
            _grassMesh = CreateGrassTuftMesh();
            _boulderMesh = CreateBoulderMesh();
            _crystalMesh = CreateCrystalClusterMesh();
        }

        private static Mesh CreateCylinderMesh(float rBot, float rTop, float h, int segs)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < segs; i++)
            {
                float a = (i / (float)segs) * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts.Add(new Vector3(c * rBot, 0f, s * rBot));
                verts.Add(new Vector3(c * rTop, h, s * rTop));
            }
            for (int i = 0; i < segs; i++)
            {
                int i0 = i * 2, i1 = i0 + 1;
                int n0 = ((i + 1) % segs) * 2, n1 = n0 + 1;
                tris.Add(i0); tris.Add(i1); tris.Add(n1);
                tris.Add(i0); tris.Add(n1); tris.Add(n0);
            }
            var mesh = new Mesh { name = "ProcPineTrunk" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreatePineCanopyMesh()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // 4 stacked Nordic pine bough cones
            (float yBase, float height, float radius, int segs)[] tiers =
            {
                (1.5f, 2.4f, 1.95f, 8),
                (2.8f, 2.2f, 1.55f, 8),
                (4.0f, 1.9f, 1.15f, 7),
                (5.0f, 1.6f, 0.70f, 6),
            };
            foreach (var (yBase, height, radius, segs) in tiers)
            {
                int tipIdx = verts.Count;
                verts.Add(new Vector3(0f, yBase + height, 0f));
                int ringStart = verts.Count;
                for (int i = 0; i < segs; i++)
                {
                    float a = (i / (float)segs) * Mathf.PI * 2f + yBase;
                    float r = radius * (i % 2 == 0 ? 1.0f : 0.84f);
                    verts.Add(new Vector3(Mathf.Cos(a) * r, yBase, Mathf.Sin(a) * r));
                }
                for (int i = 0; i < segs; i++)
                {
                    int cur = ringStart + i;
                    int next = ringStart + ((i + 1) % segs);
                    tris.Add(tipIdx); tris.Add(next); tris.Add(cur);
                }
            }
            var mesh = new Mesh { name = "ProcPineCanopy" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateGrassTuftMesh()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // 3 crossed tapered blades forming a lush grass tuft
            for (int b = 0; b < 3; b++)
            {
                float ang = (b * 60f) * Mathf.Deg2Rad;
                Vector3 right = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 0.34f;
                Vector3 lean = new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * 0.14f;
                int v0 = verts.Count;
                verts.Add(-right);
                verts.Add(right);
                verts.Add(lean + Vector3.up * 0.72f);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
            }
            var mesh = new Mesh { name = "ProcGrassTuft" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateBoulderMesh()
        {
            // Faceted icosahedron deformed organically into a natural boulder
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            Vector3[] baseVerts =
            {
                new(-1,  t,  0), new( 1,  t,  0), new(-1, -t,  0), new( 1, -t,  0),
                new( 0, -1,  t), new( 0,  1,  t), new( 0, -1, -t), new( 0,  1, -t),
                new( t,  0, -1), new( t,  0,  1), new(-t,  0, -1), new(-t,  0,  1),
            };
            int[] baseTris =
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            var verts = new Vector3[baseVerts.Length];
            for (int i = 0; i < baseVerts.Length; i++)
            {
                Vector3 n = baseVerts[i].normalized;
                float jag = 0.82f + 0.28f * Mathf.Sin(n.x * 5f + n.y * 3f + n.z * 7f);
                verts[i] = n * jag;
            }
            var mesh = new Mesh { name = "ProcBoulder" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(baseTris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateCrystalClusterMesh()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            (Vector3 dir, float len, float rad)[] shards =
            {
                (Vector3.up, 1.8f, 0.24f),
                (new Vector3(0.35f, 0.92f, 0.15f).normalized, 1.35f, 0.18f),
                (new Vector3(-0.32f, 0.90f, -0.25f).normalized, 1.2f, 0.17f),
                (new Vector3(0.15f, 0.85f, -0.45f).normalized, 1.0f, 0.14f),
            };
            foreach (var (dir, len, rad) in shards)
            {
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, dir);
                int tip = verts.Count;
                verts.Add(rot * (Vector3.up * len));
                int ring = verts.Count;
                for (int i = 0; i < 5; i++)
                {
                    float a = (i / 5f) * Mathf.PI * 2f;
                    verts.Add(rot * new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad));
                }
                for (int i = 0; i < 5; i++)
                {
                    int c = ring + i;
                    int n = ring + ((i + 1) % 5);
                    tris.Add(tip); tris.Add(n); tris.Add(c);
                }
            }
            var mesh = new Mesh { name = "ProcCrystalCluster" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
