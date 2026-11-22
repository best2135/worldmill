using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WorldMill.Editor
{
    /// <summary>
    /// Generates a 1024m x 1024m native Unity 6 HDRP Terrain using:
    /// 1. Domain-warped ridged alpine mountain chains + rolling valleys + southern ocean fjord
    /// 2. Carved meandering river channel + winding dirt roads between landmarks
    /// 3. Authored flat plateau pads for Oakhaven Hamlet & Ancient Rune Sanctuary
    /// 4. 140,000-droplet Hydraulic Erosion + Thermal Talus Erosion (sharp ridges & drainage gullies)
    /// 5. 4-layer HDRP/TerrainLit PBR Splatmap driven by elevation, slope, erosion flow & roads
    /// 6. Native Unity Terrain sculpted pine trees with organic grove clustering & spacing
    /// </summary>
    public static class WorldMillTerrainGenerator
    {
        public const float WorldSize = 1024f;
        public const float TerrainBaseY = -12f; // Water/Ocean sits at world y = 0.0
        public const float MaxTerrainHeight = 148f; // World Y spans [-12, +136]
        private const int Res = 513;
        private const int AlphaRes = 512;

        public static readonly Vector2 HamletCenter    = new(512f, 460f);
        public static readonly Vector2 BridgeCenter    = new(448f, 492f);
        public static readonly Vector2 SanctuaryCenter = new(618f, 588f);
        public static readonly Vector2 SummitCenter    = new(540f, 760f);

        public static Terrain BuildErodedTerrain(
            PbrLayerTextures granite,
            PbrLayerTextures meadow,
            PbrLayerTextures sandstone,
            PbrLayerTextures snow,
            GameObject treePrefabA,
            GameObject treePrefabB)
        {
            Directory.CreateDirectory("Assets/WorldMill/Generated/Terrain");

            float[] h = new float[Res * Res]; // Heights in meters [0..MaxTerrainHeight]
            float[] protect = new float[Res * Res]; // 1 = keep plateau intact during erosion
            float[] roadMask = new float[Res * Res];
            float[] riverMask = new float[Res * Res];

            // 1. Macro landform + river + flat POI plateaus + dirt roads
            for (int y = 0; y < Res; y++)
            {
                float wz = (y / (float)(Res - 1)) * WorldSize;
                for (int x = 0; x < Res; x++)
                {
                    float wx = (x / (float)(Res - 1)) * WorldSize;
                    int idx = y * Res + x;

                    // Domain warp for organic geological folds
                    float warpX = (Fbm(wx * 0.0022f + 11.3f, wz * 0.0022f + 5.7f, 3) - 0.5f) * 140f;
                    float warpZ = (Fbm(wx * 0.0022f + 37.1f, wz * 0.0022f + 19.4f, 3) - 0.5f) * 140f;
                    float ux = wx + warpX;
                    float uz = wz + warpZ;

                    // North rises into dramatic snow-capped alpine range; South drops to fjord ocean
                    float northFactor = Mathf.SmoothStep(240f, 820f, wz);
                    float eastRidge = Mathf.SmoothStep(560f, 920f, wx) * 0.65f;
                    float westRidge = Mathf.SmoothStep(360f, 60f, wx) * 0.55f;
                    float mountainMask = Mathf.Clamp01(northFactor * 0.85f + eastRidge + westRidge);

                    float rolling = Fbm(ux * 0.0042f, uz * 0.0042f, 4) * 28f;
                    float ridged = RidgedFbm(ux * 0.0036f + 3.1f, uz * 0.0036f + 7.4f, 5);
                    float fine = (Fbm(wx * 0.018f, wz * 0.018f, 3) - 0.5f) * 4.5f;

                    // Base elevation in world Y (ocean is y = 0)
                    float coastDrop = Mathf.SmoothStep(220f, 385f, wz);
                    float worldY = Mathf.Lerp(-10.5f, 10f + rolling, coastDrop)
                                 + ridged * ridged * (96f * mountainMask)
                                 + fine * (0.35f + 0.65f * mountainMask);

                    // 2. Carve meandering scenic river from northern pass past BridgeCenter into southern ocean
                    float riverCenterX = RiverXAtZ(wz);
                    float dRiver = Mathf.Abs(wx - riverCenterX);
                    float riverHalfWidth = 11.5f + 3.5f * Mathf.Sin(wz * 0.012f);
                    float valleyWidth = 46f;
                    if (wz < 860f)
                    {
                        float valleyT = 1f - SmoothStep(riverHalfWidth, valleyWidth, dRiver);
                        float channelT = 1f - SmoothStep(riverHalfWidth * 0.35f, riverHalfWidth, dRiver);
                        float riverBedY = Mathf.Lerp(-3.8f, 8.0f, Mathf.SmoothStep(560f, 860f, wz));
                        worldY = Mathf.Lerp(worldY, Mathf.Min(worldY, riverBedY + 7.5f), valleyT * 0.68f);
                        worldY = Mathf.Lerp(worldY, riverBedY, channelT);
                        riverMask[idx] = Mathf.Clamp01(channelT + valleyT * 0.35f);
                    }

                    // 3. Smooth authored plateau pads so buildings & ruins sit dead-flush!
                    float pHamlet = PlateauBlend(wx, wz, HamletCenter, 28f, 64f, ref worldY, 14.0f);
                    float pBridgeApproach = PlateauBlend(wx, wz, new Vector2(465f, 492f), 8f, 22f, ref worldY, 3.2f);
                    float pSanctuary = PlateauBlend(wx, wz, SanctuaryCenter, 16f, 46f, ref worldY, 38.0f);
                    protect[idx] = Mathf.Clamp01(Mathf.Max(pHamlet, Mathf.Max(pBridgeApproach, pSanctuary)));

                    // 4. Dirt Road network: Hamlet -> River Bridge, Hamlet -> Rune Sanctuary
                    float dRoad1 = DistToSegment(new Vector2(wx, wz), HamletCenter, new Vector2(464f, 492f));
                    float dRoad2 = DistToSegment(new Vector2(wx, wz), HamletCenter, SanctuaryCenter);
                    float roadWobble = (Fbm(wx * 0.04f, wz * 0.04f, 2) - 0.5f) * 3.5f;
                    float r1 = 1f - SmoothStep(2.2f, 5.2f, dRoad1 + roadWobble);
                    float r2 = 1f - SmoothStep(2.0f, 4.8f, dRoad2 + roadWobble);
                    roadMask[idx] = Mathf.Clamp01(Mathf.Max(r1, r2));

                    h[idx] = Mathf.Clamp(worldY - TerrainBaseY, 0.2f, MaxTerrainHeight - 2f);
                }
            }

            // 5. GPU-style Hydraulic Droplet Erosion (140,000 droplets carving sharp ridges & gullies!)
            float[] flowMap = new float[Res * Res];
            float[] sedimentMap = new float[Res * Res];
            RunHydraulicErosion(h, protect, flowMap, sedimentMap, 140_000, seed: 42);
            RunThermalErosion(h, protect, iterations: 6, talusAngle: 0.72f);

            // 6. Build Unity TerrainData Asset
            var tData = new TerrainData
            {
                heightmapResolution = Res,
                alphamapResolution = AlphaRes,
                size = new Vector3(WorldSize, MaxTerrainHeight, WorldSize),
            };

            float[,] heights2D = new float[Res, Res];
            for (int y = 0; y < Res; y++)
                for (int x = 0; x < Res; x++)
                    heights2D[y, x] = Mathf.Clamp01(h[y * Res + x] / MaxTerrainHeight);
            tData.SetHeights(0, 0, heights2D);

            // 7. Create 4 HDRP TerrainLayers using our generated 512x512 PBR textures
            var lMeadow = CreateTerrainLayer("Assets/WorldMill/Generated/Terrain/Layer_Meadow.terrainlayer", meadow, 12f, 0.14f);
            var lGranite = CreateTerrainLayer("Assets/WorldMill/Generated/Terrain/Layer_Granite.terrainlayer", granite, 18f, 0.28f);
            var lSand = CreateTerrainLayer("Assets/WorldMill/Generated/Terrain/Layer_Sandstone.terrainlayer", sandstone, 10f, 0.22f);
            var lSnow = CreateTerrainLayer("Assets/WorldMill/Generated/Terrain/Layer_Snow.terrainlayer", snow, 14f, 0.42f);
            tData.terrainLayers = new[] { lMeadow, lGranite, lSand, lSnow };

            // 8. Compute Geological Splatmap (slope + altitude + hydraulic flow + sediment + dirt roads!)
            float[,,] alpha = new float[AlphaRes, AlphaRes, 4];
            for (int y = 0; y < AlphaRes; y++)
            {
                float ny = y / (float)(AlphaRes - 1);
                int hy = Mathf.Clamp(Mathf.RoundToInt(ny * (Res - 1)), 0, Res - 1);
                for (int x = 0; x < AlphaRes; x++)
                {
                    float nx = x / (float)(AlphaRes - 1);
                    int hx = Mathf.Clamp(Mathf.RoundToInt(nx * (Res - 1)), 0, Res - 1);
                    int idx = hy * Res + hx;

                    float worldY = h[idx] + TerrainBaseY;
                    float steepness = tData.GetSteepness(nx, ny); // degrees [0..90]
                    float flow = Mathf.Clamp01(flowMap[idx] * 0.45f);
                    float sed = Mathf.Clamp01(sedimentMap[idx] * 0.9f);
                    float road = roadMask[idx];
                    float river = riverMask[idx];

                    float wSnow = SmoothStep(54f, 70f, worldY) * (1f - SmoothStep(40f, 58f, steepness));
                    float wRock = SmoothStep(22f, 38f, steepness) + flow * 0.55f;
                    float wSand = Mathf.Max(
                        1f - SmoothStep(-0.5f, 3.2f, worldY),
                        Mathf.Max(road * 0.95f, Mathf.Max(river * 0.75f, sed * 0.45f))
                    ) * (1f - SmoothStep(34f, 48f, steepness));
                    float wGrass = Mathf.Max(0.05f, 1f - Mathf.Clamp01(wRock * 0.95f + wSand * 0.9f + wSnow));

                    // Keep Oakhaven village square a pleasant blend of meadow & trampled earth
                    float dHamlet = Vector2.Distance(new Vector2(nx * WorldSize, ny * WorldSize), HamletCenter);
                    if (dHamlet < 22f)
                    {
                        float sq = 1f - SmoothStep(10f, 22f, dHamlet);
                        wSand = Mathf.Max(wSand, sq * 0.55f);
                    }

                    float sum = wGrass + wRock + wSand + wSnow;
                    if (sum < 1e-4f) { wGrass = 1f; sum = 1f; }
                    alpha[y, x, 0] = wGrass / sum;
                    alpha[y, x, 1] = wRock / sum;
                    alpha[y, x, 2] = wSand / sum;
                    alpha[y, x, 3] = wSnow / sum;
                }
            }
            tData.SetAlphamaps(0, 0, alpha);

            // 9. Populate Native Unity Terrain Sculpted Pine Forests (with spacing + road/village/river exclusion!)
            if (treePrefabA != null)
            {
                var protos = treePrefabB != null
                    ? new[] { new TreePrototype { prefab = treePrefabA }, new TreePrototype { prefab = treePrefabB } }
                    : new[] { new TreePrototype { prefab = treePrefabA } };
                tData.treePrototypes = protos;
                tData.RefreshPrototypes();

                float treeBaseScale = 4.2f;
                var mf = treePrefabA.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.bounds.size.y > 0.05f)
                    treeBaseScale = 7.2f / mf.sharedMesh.bounds.size.y;

                var rng = new System.Random(1337);
                var instances = new List<TreeInstance>(4000);
                // Grid jitter sampling (9m cells -> guaranteed clean spacing & scenic clearings!)
                float cell = 9.0f;
                for (float wz = 260f; wz < 880f; wz += cell)
                {
                    for (float wx = 120f; wx < 900f; wx += cell)
                    {
                        float px = wx + ((float)rng.NextDouble() - 0.5f) * cell * 0.72f;
                        float pz = wz + ((float)rng.NextDouble() - 0.5f) * cell * 0.72f;
                        float nx = px / WorldSize;
                        float nz = pz / WorldSize;

                        int hx = Mathf.Clamp(Mathf.RoundToInt(nx * (Res - 1)), 0, Res - 1);
                        int hy = Mathf.Clamp(Mathf.RoundToInt(nz * (Res - 1)), 0, Res - 1);
                        int idx = hy * Res + hx;

                        // Keep village, sanctuary, roads, river, beaches & snow peaks clear of trees
                        if (protect[idx] > 0.25f || roadMask[idx] > 0.15f || riverMask[idx] > 0.35f) continue;

                        float worldY = h[idx] + TerrainBaseY;
                        if (worldY < 2.8f || worldY > 52f) continue;

                        float steep = tData.GetSteepness(nx, nz);
                        if (steep > 25f) continue;

                        // Coherent forest groves with open sunlit meadows between them
                        float grove = Fbm(px * 0.0085f + 4.1f, pz * 0.0085f + 9.3f, 3);
                        float chance = grove > 0.50f ? 0.62f : 0.06f;
                        if ((float)rng.NextDouble() > chance) continue;

                        float normY = h[idx] / MaxTerrainHeight;
                        float scale = treeBaseScale * Mathf.Lerp(0.80f, 1.38f, (float)rng.NextDouble());
                        instances.Add(new TreeInstance
                        {
                            position = new Vector3(nx, normY, nz),
                            widthScale = scale,
                            heightScale = scale * Mathf.Lerp(0.92f, 1.24f, (float)rng.NextDouble()),
                            rotation = (float)rng.NextDouble() * Mathf.PI * 2f,
                            color = Color.white,
                            lightmapColor = Color.white,
                            prototypeIndex = protos.Length > 1 && rng.NextDouble() > 0.5 ? 1 : 0,
                        });
                    }
                }
                tData.SetTreeInstances(instances.ToArray(), snapToHeightmap: true);
                Debug.Log($"[WorldMill Terrain] Populated {instances.Count} native Terrain pine trees across eroded valley.");
            }

            string assetPath = "Assets/WorldMill/Generated/Terrain/WorldMillTerrainData.asset";
            if (AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(tData, assetPath);

            var terrainGo = Terrain.CreateTerrainGameObject(tData);
            terrainGo.name = "WorldMill Eroded Terrain";
            terrainGo.transform.position = new Vector3(0f, TerrainBaseY, 0f);
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.heightmapPixelError = 3f;
            terrain.basemapDistance = 450f;
            terrain.treeDistance = 480f;
            terrain.treeBillboardDistance = 180f;
            terrain.treeMaximumFullLODCount = 1500;
            terrain.drawInstanced = true;

            var terrainLit = Shader.Find("HDRP/TerrainLit");
            if (terrainLit != null)
            {
                var tMat = new Material(terrainLit) { name = "WorldMillHDRPTerrainLit" };
                string tMatPath = "Assets/WorldMill/Generated/Terrain/WorldMillHDRPTerrainLit.mat";
                if (AssetDatabase.LoadAssetAtPath<Material>(tMatPath) != null)
                    AssetDatabase.DeleteAsset(tMatPath);
                AssetDatabase.CreateAsset(tMat, tMatPath);
                terrain.materialTemplate = tMat;
            }

            return terrain;
        }

        public static float RiverXAtZ(float wz)
        {
            // Smooth S-curve meandering river passing right through BridgeCenter (448, 492)
            float dz = wz - BridgeCenter.y;
            return BridgeCenter.x + Mathf.Sin(dz * 0.0085f) * 44f + Mathf.Sin(dz * 0.0032f) * 28f;
        }

        private static TerrainLayer CreateTerrainLayer(string path, PbrLayerTextures tex, float tileSize, float smoothness)
        {
            if (AssetDatabase.LoadAssetAtPath<TerrainLayer>(path) != null)
                AssetDatabase.DeleteAsset(path);
            var layer = new TerrainLayer
            {
                diffuseTexture = tex.BaseColor,
                normalMapTexture = tex.Normal,
                maskMapTexture = tex.Mask,
                normalScale = 1.25f,
                smoothness = smoothness,
                metallic = 0f,
                tileSize = new Vector2(tileSize, tileSize),
            };
            AssetDatabase.CreateAsset(layer, path);
            return layer;
        }

        private static float PlateauBlend(float wx, float wz, Vector2 center, float innerR, float outerR, ref float worldY, float targetY)
        {
            float d = Vector2.Distance(new Vector2(wx, wz), center);
            if (d >= outerR) return 0f;
            float t = 1f - SmoothStep(innerR, outerR, d);
            worldY = Mathf.Lerp(worldY, targetY, t);
            return t;
        }

        private static void RunHydraulicErosion(float[] map, float[] protect, float[] flow, float[] sed, int droplets, int seed)
        {
            var rng = new System.Random(seed);
            const float inertia = 0.08f;
            const float sedimentCapacityFactor = 4.2f;
            const float minSedimentCapacity = 0.01f;
            const float erodeSpeed = 0.32f;
            const float depositSpeed = 0.30f;
            const float evaporateSpeed = 0.015f;
            const float gravity = 4.0f;
            const int maxSteps = 38;

            for (int d = 0; d < droplets; d++)
            {
                float px = 2f + (float)rng.NextDouble() * (Res - 5);
                float py = 2f + (float)rng.NextDouble() * (Res - 5);
                float dirX = 0f, dirY = 0f;
                float speed = 1f;
                float water = 1f;
                float carried = 0f;

                for (int step = 0; step < maxSteps; step++)
                {
                    int nx = (int)px;
                    int ny = (int)py;
                    if (nx < 1 || ny < 1 || nx >= Res - 2 || ny >= Res - 2) break;

                    float ox = px - nx;
                    float oy = py - ny;
                    int idx = ny * Res + nx;

                    float h00 = map[idx];
                    float h10 = map[idx + 1];
                    float h01 = map[idx + Res];
                    float h11 = map[idx + Res + 1];

                    float gradX = (h10 - h00) * (1f - oy) + (h11 - h01) * oy;
                    float gradY = (h01 - h00) * (1f - ox) + (h11 - h10) * ox;
                    float curH = h00 * (1f - ox) * (1f - oy) + h10 * ox * (1f - oy) + h01 * (1f - ox) * oy + h11 * ox * oy;

                    dirX = dirX * inertia - gradX * (1f - inertia);
                    dirY = dirY * inertia - gradY * (1f - inertia);
                    float len = Mathf.Sqrt(dirX * dirX + dirY * dirY);
                    if (len < 1e-5f)
                    {
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                        dirX = Mathf.Cos(a);
                        dirY = Mathf.Sin(a);
                    }
                    else
                    {
                        dirX /= len;
                        dirY /= len;
                    }

                    px += dirX;
                    py += dirY;
                    if (px < 1f || py < 1f || px >= Res - 2f || py >= Res - 2f) break;

                    int nnx = (int)px;
                    int nny = (int)py;
                    float nox = px - nnx;
                    float noy = py - nny;
                    int nidx = nny * Res + nnx;
                    float newH = map[nidx] * (1f - nox) * (1f - noy) + map[nidx + 1] * nox * (1f - noy)
                               + map[nidx + Res] * (1f - nox) * noy + map[nidx + Res + 1] * nox * noy;

                    float deltaH = newH - curH;
                    float capacity = Mathf.Max(-deltaH * speed * water * sedimentCapacityFactor, minSedimentCapacity);
                    float mask = 1f - protect[idx];

                    if (carried > capacity || deltaH > 0f)
                    {
                        float deposit = (deltaH > 0f) ? Mathf.Min(deltaH, carried) : (carried - capacity) * depositSpeed;
                        deposit *= mask;
                        carried -= deposit;
                        map[idx] += deposit * (1f - ox) * (1f - oy);
                        map[idx + 1] += deposit * ox * (1f - oy);
                        map[idx + Res] += deposit * (1f - ox) * oy;
                        map[idx + Res + 1] += deposit * ox * oy;
                        sed[idx] += deposit;
                    }
                    else
                    {
                        float erode = Mathf.Min((capacity - carried) * erodeSpeed, -deltaH) * mask;
                        carried += erode;
                        // 3x3 soft brush prevents spiky single-pixel pits
                        float center = erode * 0.52f;
                        float side = erode * 0.12f;
                        map[idx] = Mathf.Max(0.1f, map[idx] - center);
                        map[idx - 1] = Mathf.Max(0.1f, map[idx - 1] - side);
                        map[idx + 1] = Mathf.Max(0.1f, map[idx + 1] - side);
                        map[idx - Res] = Mathf.Max(0.1f, map[idx - Res] - side);
                        map[idx + Res] = Mathf.Max(0.1f, map[idx + Res] - side);
                        flow[idx] += erode;
                    }

                    speed = Mathf.Sqrt(Mathf.Max(0.01f, speed * speed + Mathf.Max(0f, -deltaH) * gravity));
                    water *= (1f - evaporateSpeed);
                }
            }
        }

        private static void RunThermalErosion(float[] map, float[] protect, int iterations, float talusAngle)
        {
            float[] delta = new float[Res * Res];
            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(delta, 0, delta.Length);
                for (int y = 1; y < Res - 1; y++)
                {
                    for (int x = 1; x < Res - 1; x++)
                    {
                        int i = y * Res + x;
                        float mask = 1f - protect[i];
                        if (mask < 0.05f) continue;

                        float hc = map[i];
                        int[] nbs = { i - 1, i + 1, i - Res, i + Res };
                        float maxDiff = 0f;
                        float totalExcess = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            float d = hc - map[nbs[k]];
                            if (d > talusAngle)
                            {
                                totalExcess += d - talusAngle;
                                if (d > maxDiff) maxDiff = d;
                            }
                        }
                        if (totalExcess > 0f)
                        {
                            float move = 0.35f * (maxDiff - talusAngle) * mask;
                            delta[i] -= move;
                            for (int k = 0; k < 4; k++)
                            {
                                float d = hc - map[nbs[k]];
                                if (d > talusAngle)
                                    delta[nbs[k]] += move * ((d - talusAngle) / totalExcess);
                            }
                        }
                    }
                }
                for (int i = 0; i < map.Length; i++)
                    map[i] = Mathf.Clamp(map[i] + delta[i], 0.1f, MaxTerrainHeight - 1f);
            }
        }

        private static float SmoothStep(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-4f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return Vector2.Distance(p, a + ab * t);
        }

        private static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Mathf.PerlinNoise(x * freq + 17.3f * i, y * freq + 31.7f * i);
                norm += amp;
                freq *= 2.03f;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        private static float RidgedFbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.55f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Mathf.PerlinNoise(x * freq + 13.1f * i, y * freq + 29.3f * i) * 2f - 1f);
                sum += amp * (n * n);
                norm += amp;
                freq *= 2.12f;
                amp *= 0.48f;
            }
            return sum / norm;
        }
    }
}
