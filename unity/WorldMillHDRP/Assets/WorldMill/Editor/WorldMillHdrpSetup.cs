using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using WorldMill.UI;

namespace WorldMill.Editor
{
    public static class WorldMillHdrpSetup
    {
        public const string ScenePath = "Assets/Scenes/WorldMillHDRP.unity";

        [MenuItem("WorldMill/Build HDRP WorldMill Scene")]
        public static void BuildScene()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });

            Directory.CreateDirectory("Assets/WorldMill/Generated");
            Directory.CreateDirectory("Assets/WorldMill/Textures");
            Directory.CreateDirectory("Assets/Scenes");

            // 1. Create & configure HDRenderPipelineAsset (with Water, Volumetric Clouds, Volumetric Fog, SSAO, SSR)
            var hdrpAsset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            AssetDatabase.CreateAsset(hdrpAsset, "Assets/WorldMill/Generated/WorldMillHDRPAsset.asset");

            var so = new SerializedObject(hdrpAsset);
            SetBool(so, "m_RenderPipelineSettings.supportWater", true);
            SetBool(so, "m_RenderPipelineSettings.supportVolumetrics", true);
            SetBool(so, "m_RenderPipelineSettings.supportVolumetricClouds", true);
            SetBool(so, "m_RenderPipelineSettings.supportSSAO", true);
            SetBool(so, "m_RenderPipelineSettings.supportSSR", true);
            SetBool(so, "m_RenderPipelineSettings.supportSubsurfaceScattering", true);
            SetBool(so, "m_RenderPipelineSettings.supportContactShadows", true);
            SetBool(so, "m_RenderPipelineSettings.supportShadowMask", false);
            so.ApplyModifiedPropertiesWithoutUndo();

            GraphicsSettings.defaultRenderPipeline = hdrpAsset;
            QualitySettings.renderPipeline = hdrpAsset;

            // 2. Generate 4 seamless 512x512 PBR Texture Sets (Granite Rock, Alpine Grass, Sandstone, Alpine Snow)
            var texDir = "Assets/WorldMill/Textures";
            var layer0 = WorldMillPbrTextureGen.GenerateLayer(texDir, "L0_Granite", 0);
            var layer1 = WorldMillPbrTextureGen.GenerateLayer(texDir, "L1_Meadow", 1);
            var layer2 = WorldMillPbrTextureGen.GenerateLayer(texDir, "L2_Sandstone", 2);
            var layer3 = WorldMillPbrTextureGen.GenerateLayer(texDir, "L3_Snow", 3);

            // 3. Configure native HDRP/LayeredLit Triplanar Height-Blended 4-Layer PBR Material
            var layeredShader = Shader.Find("HDRP/LayeredLit");
            if (layeredShader == null)
                throw new System.InvalidOperationException("Shader HDRP/LayeredLit not found — verify HDRP package is installed.");

            var terrainMat = new Material(layeredShader) { name = "WorldMillHDRPTerrain" };
            terrainMat.SetFloat("_LayerCount", 4f);
            terrainMat.SetFloat("_VertexColorMode", 1f); // Multiply with white LayerMaskMap -> RGB drives layers 1..3!
            terrainMat.SetFloat("_UseHeightBasedBlend", 1f);
            terrainMat.SetFloat("_HeightTransition", 0.28f);
            terrainMat.SetFloat("_ReceivesSSR", 1f);
            terrainMat.SetFloat("_EnableGeometricSpecularAA", 1f);

            AssignLayer(terrainMat, 0, layer0, 0.08f, 1.35f); // Cliff Granite / Basalt
            AssignLayer(terrainMat, 1, layer1, 0.14f, 1.05f); // Lush Meadow Grass
            AssignLayer(terrainMat, 2, layer2, 0.11f, 1.15f); // Canyon Sandstone / Shore
            AssignLayer(terrainMat, 3, layer3, 0.10f, 0.85f); // Alpine Snow / Ice

            HDMaterial.ValidateMaterial(terrainMat);
            AssetDatabase.CreateAsset(terrainMat, "Assets/WorldMill/Generated/WorldMillHDRPTerrain.mat");

            // 4. Global HDRP Sky, Volumetric Clouds, Volumetric Fog, SSAO, Contact Shadows, SSR, ACES & Bloom Volume
            var volProfile = ScriptableObject.CreateInstance<VolumeProfile>();

            var visEnv = volProfile.Add<VisualEnvironment>(true);
            visEnv.skyType.Override((int)SkyType.PhysicallyBased);
            visEnv.skyAmbientMode.Override(SkyAmbientMode.Dynamic);

            var pbsSky = volProfile.Add<PhysicallyBasedSky>(true);
            pbsSky.type.Override(PhysicallyBasedSkyModel.EarthAdvanced);

            var clouds = volProfile.Add<VolumetricClouds>(true);
            clouds.enable.Override(true);
            clouds.cloudPreset = VolumetricClouds.CloudPresets.Cloudy;

            var fog = volProfile.Add<Fog>(true);
            fog.enabled.Override(true);
            fog.enableVolumetricFog.Override(true);
            fog.meanFreePath.Override(320f);
            fog.baseHeight.Override(-14f);
            fog.maximumHeight.Override(130f);

            var waterVol = volProfile.Add<WaterRendering>(true);
            waterVol.enable.Override(true);

            var ssao = volProfile.Add<ScreenSpaceAmbientOcclusion>(true);
            ssao.intensity.Override(0.95f);

            var contactShadows = volProfile.Add<ContactShadows>(true);
            contactShadows.enable.Override(true);

            var ssr = volProfile.Add<ScreenSpaceReflection>(true);
            ssr.enabled.Override(true);

            var exposure = volProfile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.Automatic);
            exposure.limitMin.Override(9.2f);
            exposure.limitMax.Override(14.8f);

            var tonemap = volProfile.Add<Tonemapping>(true);
            tonemap.mode.Override(TonemappingMode.ACES);

            var bloom = volProfile.Add<Bloom>(true);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.32f);
            bloom.scatter.Override(0.70f);

            AssetDatabase.CreateAsset(volProfile, "Assets/WorldMill/Generated/WorldMillHDRPVolume.asset");

            // 5. Build HDRP Scene
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Global HDRP Sky & Post-Processing Volume
            var volGo = new GameObject("HDRP Sky & Atmosphere Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = volProfile;

            // Physical Directional Sun (100,000 lux + HDRP Cascaded Shadows + Volumetric God-Rays)
            var sunGo = new GameObject("Physical Directional Sun");
            var sunLight = sunGo.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.useColorTemperature = true;
            sunLight.colorTemperature = 5400f;
            var hdSun = sunGo.AddComponent<HDAdditionalLightData>();
            hdSun.SetIntensity(95000f, LightUnit.Lux);
            hdSun.EnableShadows(true);
            hdSun.SetShadowResolutionLevel(2); // High resolution cascaded shadows
            sunGo.transform.rotation = Quaternion.Euler(28f, -18f, 0f);
            RenderSettings.sun = sunLight;

            // Native Unity 6 HDRP Infinite FFT Ocean at y = -6.0
            var waterGo = new GameObject("HDRP Infinite Ocean");
            waterGo.transform.position = new Vector3(0f, -6.0f, 0f);
            var water = waterGo.AddComponent<WaterSurface>();
            water.surfaceType = WaterSurfaceType.OceanSeaLake;
            water.geometryType = WaterGeometryType.Infinite;

            // WorldMill Rust + wgpu Streamer
            var streamerGo = new GameObject("WorldMill Streamer");
            var streamer = streamerGo.AddComponent<WorldMillStreamer>();
            streamer.terrainMaterial = terrainMat;

            // GPU-Instanced Nordic Pine Forests, Meadow Grass, Boulders & Glowing Cave Crystals
            var foliageGo = new GameObject("WorldMill Foliage System");
            var foliage = foliageGo.AddComponent<WorldMillFoliageSystem>();
            foliage.streamer = streamer;
            foliage.waterLevel = -6.0f;

            // Main HDRP Camera with TAA
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(12f, 32f, -42f);
            camGo.transform.rotation = Quaternion.Euler(14f, 0f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 800f;
            var hdCam = camGo.AddComponent<HDAdditionalCameraData>();
            hdCam.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;

            // Third-Person RPG Adventurer Player
            var playerGo = new GameObject("Player Adventurer");
            playerGo.transform.position = new Vector3(12f, 26f, -38f);
            var player = playerGo.AddComponent<WorldMillPlayerController>();
            player.streamer = streamer;
            player.sunLight = sunLight;
            player.playerCamera = cam;

            // Witcher x Valheim RPG Client (Quests, Lore, Persistent Campfires) & HUD
            var rpgGo = new GameObject("WorldMill RPG Systems");
            var rpgClient = rpgGo.AddComponent<WorldMillRpgClient>();
            rpgClient.streamer = streamer;
            rpgClient.playerTransform = playerGo.transform;

            var rpgHud = rpgGo.AddComponent<WorldMillRpgHud>();
            rpgHud.rpgClient = rpgClient;
            rpgHud.streamer = streamer;
            rpgHud.playerTransform = playerGo.transform;

            player.rpgClient = rpgClient;
            player.rpgHud = rpgHud;

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[WorldMill HDRP] Built and saved HDRP scene successfully at " + ScenePath);
        }

        private static void AssignLayer(Material mat, int idx, PbrLayerTextures tex, float worldScale, float normalScale)
        {
            mat.SetColor($"_BaseColor{idx}", Color.white);
            mat.SetTexture($"_BaseColorMap{idx}", tex.BaseColor);
            mat.SetTexture($"_NormalMap{idx}", tex.Normal);
            mat.SetTexture($"_MaskMap{idx}", tex.Mask);
            mat.SetTexture($"_HeightMap{idx}", tex.Height);
            mat.SetFloat($"_NormalScale{idx}", normalScale);
            mat.SetFloat($"_UVBase{idx}", 5f); // 5 = Triplanar in HDRP/LayeredLit!
            mat.SetFloat($"_TexWorldScale{idx}", worldScale);
        }

        private static void SetBool(SerializedObject so, string propPath, bool value)
        {
            var prop = so.FindProperty(propPath);
            if (prop != null)
                prop.boolValue = value;
            else
                Debug.LogWarning($"[WorldMill HDRP] Property not found on HDRenderPipelineAsset: {propPath}");
        }
    }
}
