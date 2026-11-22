using System.IO;
using UnityEditor;
using UnityEngine;

namespace WorldMill.Editor
{
    public struct PbrLayerTextures
    {
        public Texture2D BaseColor;
        public Texture2D Normal;
        public Texture2D Mask;
        public Texture2D Height;
    }

    /// <summary>
    /// Generates seamless 512x512 tileable PBR texture sets (BaseColor, Tangent NormalMap,
    /// HDRP MaskMap [R=Metallic, G=AO, B=Detail, A=Smoothness], and HeightMap) for HDRP/LayeredLit.
    /// </summary>
    public static class WorldMillPbrTextureGen
    {
        private const int Res = 512;

        public static PbrLayerTextures GenerateLayer(string dir, string name, int style)
        {
            Directory.CreateDirectory(dir);
            var heightField = new float[Res * Res];
            var albedoTex = new Texture2D(Res, Res, TextureFormat.RGBA32, false);
            var maskTex = new Texture2D(Res, Res, TextureFormat.RGBA32, false);
            var heightTex = new Texture2D(Res, Res, TextureFormat.RGBA32, false);
            var normalTex = new Texture2D(Res, Res, TextureFormat.RGBA32, false);

            var albedoCols = new Color[Res * Res];
            var maskCols = new Color[Res * Res];
            var heightCols = new Color[Res * Res];

            for (int y = 0; y < Res; y++)
            {
                float v = (float)y / Res;
                for (int x = 0; x < Res; x++)
                {
                    float u = (float)x / Res;
                    int idx = y * Res + x;

                    float fMacro = TileFbm(u, v, 4, 4, style * 17 + 3);
                    float fMid = TileFbm(u, v, 10, 4, style * 31 + 11);
                    float fFine = TileFbm(u, v, 28, 3, style * 47 + 29);
                    float ridged = TileRidged(u, v, 8, 3, style * 59 + 7);
                    float strata = Mathf.Sin((v * 6f + fMacro * 1.4f) * Mathf.PI * 2f) * 0.5f + 0.5f;

                    float h;
                    Color col;
                    float metallic = 0.0f;
                    float ao;
                    float smoothness;

                    if (style == 0)
                    {
                        // Layer 0: Stratified Granite / Basalt Cliff Rock
                        h = Mathf.Clamp01(ridged * 0.50f + strata * 0.25f + fMid * 0.17f + fFine * 0.08f);
                        Color darkCrevice = new Color(0.11f, 0.115f, 0.135f);
                        Color graniteMid = new Color(0.34f, 0.33f, 0.31f);
                        Color warmStrata = new Color(0.46f, 0.37f, 0.29f);
                        col = Color.Lerp(darkCrevice, graniteMid, Mathf.SmoothStep(0.18f, 0.78f, h));
                        col = Color.Lerp(col, warmStrata, strata * 0.28f * fMacro);
                        col *= Mathf.Lerp(0.84f, 1.12f, fFine);
                        ao = Mathf.Lerp(0.32f, 1.0f, h);
                        smoothness = Mathf.Lerp(0.14f, 0.42f, ridged * fMid);
                    }
                    else if (style == 1)
                    {
                        // Layer 1: Lush Alpine Meadow Grass & Moss
                        float blades = TileFbm(u * 2f, v * 2f, 24, 3, 107);
                        h = Mathf.Clamp01(fMacro * 0.35f + fMid * 0.35f + blades * 0.30f);
                        Color deepMoss = new Color(0.09f, 0.24f, 0.07f);
                        Color lushGrass = new Color(0.21f, 0.42f, 0.12f);
                        Color dryTip = new Color(0.34f, 0.44f, 0.16f);
                        Color soil = new Color(0.20f, 0.15f, 0.09f);
                        col = Color.Lerp(deepMoss, lushGrass, fMacro);
                        col = Color.Lerp(col, dryTip, Mathf.SmoothStep(0.55f, 0.90f, blades) * 0.55f);
                        col = Color.Lerp(soil, col, Mathf.SmoothStep(0.18f, 0.44f, fMid));
                        ao = Mathf.Lerp(0.45f, 1.0f, h);
                        smoothness = Mathf.Lerp(0.08f, 0.24f, blades);
                    }
                    else if (style == 2)
                    {
                        // Layer 2: Warm Canyon Sandstone / Wind-Rippled Shore
                        float ripples = Mathf.Sin((u * 14f + fMacro * 2.2f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                        h = Mathf.Clamp01(ripples * 0.35f + strata * 0.30f + fMid * 0.25f + fFine * 0.10f);
                        Color ochre = new Color(0.54f, 0.36f, 0.21f);
                        Color warmSand = new Color(0.76f, 0.61f, 0.41f);
                        Color paleBuff = new Color(0.84f, 0.74f, 0.55f);
                        col = Color.Lerp(ochre, warmSand, strata * 0.65f + fMacro * 0.35f);
                        col = Color.Lerp(col, paleBuff, ripples * 0.35f);
                        col *= Mathf.Lerp(0.90f, 1.08f, fFine);
                        ao = Mathf.Lerp(0.55f, 1.0f, h);
                        smoothness = Mathf.Lerp(0.14f, 0.34f, fMid);
                    }
                    else
                    {
                        // Layer 3: Wind-Sculpted Alpine Snow & Glacial Ice
                        float drift = Mathf.Sin((u * 6f + v * 2f + fMacro * 1.8f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                        h = Mathf.Clamp01(drift * 0.45f + fMid * 0.35f + fFine * 0.20f);
                        Color iceBlue = new Color(0.76f, 0.85f, 0.96f);
                        Color pureSnow = new Color(0.95f, 0.97f, 1.00f);
                        col = Color.Lerp(iceBlue, pureSnow, Mathf.SmoothStep(0.2f, 0.75f, h));
                        ao = Mathf.Lerp(0.70f, 1.0f, h);
                        float glint = Mathf.SmoothStep(0.74f, 0.95f, fFine);
                        smoothness = Mathf.Lerp(0.32f, 0.88f, glint);
                    }

                    heightField[idx] = h;
                    albedoCols[idx] = new Color(col.r, col.g, col.b, 1f);
                    // HDRP MaskMap: R = Metallic, G = AO, B = DetailMask, A = Smoothness
                    maskCols[idx] = new Color(metallic, ao, 0.5f, smoothness);
                    heightCols[idx] = new Color(h, h, h, 1f);
                }
            }

            // Compute seamless Sobel tangent-space Normal Map from heightField
            float bumpStrength = style == 0 ? 5.2f : (style == 2 ? 3.2f : (style == 1 ? 2.6f : 1.6f));
            var normalCols = new Color[Res * Res];
            for (int y = 0; y < Res; y++)
            {
                int ym = (y - 1 + Res) % Res;
                int yp = (y + 1) % Res;
                for (int x = 0; x < Res; x++)
                {
                    int xm = (x - 1 + Res) % Res;
                    int xp = (x + 1) % Res;
                    float tl = heightField[ym * Res + xm];
                    float t  = heightField[ym * Res + x];
                    float tr = heightField[ym * Res + xp];
                    float l  = heightField[y * Res + xm];
                    float r  = heightField[y * Res + xp];
                    float bl = heightField[yp * Res + xm];
                    float b  = heightField[yp * Res + x];
                    float br = heightField[yp * Res + xp];

                    float dx = (tr + 2f * r + br) - (tl + 2f * l + bl);
                    float dy = (bl + 2f * b + br) - (tl + 2f * t + tr);
                    var n = new Vector3(-dx * bumpStrength, -dy * bumpStrength, 1f).normalized;
                    normalCols[y * Res + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }

            albedoTex.SetPixels(albedoCols); albedoTex.Apply();
            maskTex.SetPixels(maskCols);     maskTex.Apply();
            heightTex.SetPixels(heightCols); heightTex.Apply();
            normalTex.SetPixels(normalCols); normalTex.Apply();

            string pBase = $"{dir}/{name}_BaseColor.png";
            string pNorm = $"{dir}/{name}_Normal.png";
            string pMask = $"{dir}/{name}_Mask.png";
            string pHgt  = $"{dir}/{name}_Height.png";

            File.WriteAllBytes(pBase, albedoTex.EncodeToPNG());
            File.WriteAllBytes(pNorm, normalTex.EncodeToPNG());
            File.WriteAllBytes(pMask, maskTex.EncodeToPNG());
            File.WriteAllBytes(pHgt, heightTex.EncodeToPNG());

            Object.DestroyImmediate(albedoTex);
            Object.DestroyImmediate(maskTex);
            Object.DestroyImmediate(heightTex);
            Object.DestroyImmediate(normalTex);

            AssetDatabase.ImportAsset(pBase, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(pNorm, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(pMask, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(pHgt, ImportAssetOptions.ForceUpdate);

            ConfigureImporter(pBase, true, false);
            ConfigureImporter(pNorm, false, true);
            ConfigureImporter(pMask, false, false);
            ConfigureImporter(pHgt, false, false);

            return new PbrLayerTextures
            {
                BaseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(pBase),
                Normal = AssetDatabase.LoadAssetAtPath<Texture2D>(pNorm),
                Mask = AssetDatabase.LoadAssetAtPath<Texture2D>(pMask),
                Height = AssetDatabase.LoadAssetAtPath<Texture2D>(pHgt),
            };
        }

        private static void ConfigureImporter(string assetPath, bool srgb, bool isNormal)
        {
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            imp.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = srgb;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.anisoLevel = 8;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.SaveAndReimport();
        }

        private static float TileHash(int x, int y, int period, int seed)
        {
            int px = ((x % period) + period) % period;
            int py = ((y % period) + period) % period;
            uint h = (uint)(px * 374761393 + py * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0x00FFFFFFu) / 16777215f;
        }

        private static float TileNoise(float u, float v, int period, int seed)
        {
            float fx = u * period;
            float fy = v * period;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);

            float a = TileHash(x0, y0, period, seed);
            float b = TileHash(x0 + 1, y0, period, seed);
            float c = TileHash(x0, y0 + 1, period, seed);
            float d = TileHash(x0 + 1, y0 + 1, period, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        private static float TileFbm(float u, float v, int basePeriod, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            int p = basePeriod;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * TileNoise(u, v, p, seed + i * 19);
                norm += amp;
                p *= 2;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        private static float TileRidged(float u, float v, int basePeriod, int octaves, int seed)
        {
            float sum = 0f, amp = 0.55f, norm = 0f;
            int p = basePeriod;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(TileNoise(u, v, p, seed + i * 23) * 2f - 1f);
                sum += amp * (n * n);
                norm += amp;
                p *= 2;
                amp *= 0.48f;
            }
            return sum / norm;
        }
    }
}
