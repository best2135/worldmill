using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace WorldMill.Editor
{
    public static class WorldMillSceneSetup
    {
        [MenuItem("WorldMill/Build URP WorldMill Scene")]
        public static void BuildScene()
        {
            Directory.CreateDirectory("Assets/WorldMill/Generated");
            Directory.CreateDirectory("Assets/Scenes");

            // 1. Create & assign URP Pipeline Asset with Cascaded Shadows + HDR + MSAA 4x
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, "Assets/WorldMill/Generated/WorldMillRenderer.asset");

            var urpAsset = UniversalRenderPipelineAsset.Create(rendererData);
            urpAsset.supportsHDR = true;
            urpAsset.msaaSampleCount = 4;
            urpAsset.shadowDistance = 240f;
            urpAsset.shadowCascadeCount = 4;
            urpAsset.supportsCameraDepthTexture = true;
            urpAsset.supportsCameraOpaqueTexture = true;
            AssetDatabase.CreateAsset(urpAsset, "Assets/WorldMill/Generated/WorldMillURPAsset.asset");

            GraphicsSettings.defaultRenderPipeline = urpAsset;
            QualitySettings.renderPipeline = urpAsset;

            // 2. Materials (Triplanar PBR Terrain + Animated Fresnel Water + Procedural Skybox)
            var terrainShader = Shader.Find("WorldMill/TriplanarTerrainURP");
            var terrainMat = new Material(terrainShader);
            AssetDatabase.CreateAsset(terrainMat, "Assets/WorldMill/Generated/WorldMillTerrain.mat");

            var waterShader = Shader.Find("WorldMill/WaterURP");
            var waterMat = new Material(waterShader);
            AssetDatabase.CreateAsset(waterMat, "Assets/WorldMill/Generated/WorldMillWater.mat");

            var skyShader = Shader.Find("Skybox/Procedural");
            Material skyMat = null;
            if (skyShader != null)
            {
                skyMat = new Material(skyShader);
                skyMat.SetFloat("_SunSize", 0.035f);
                skyMat.SetFloat("_AtmosphereThickness", 1.15f);
                skyMat.SetColor("_SkyTint", new Color(0.45f, 0.56f, 0.72f, 1f));
                skyMat.SetColor("_GroundColor", new Color(0.24f, 0.28f, 0.34f, 1f));
                skyMat.SetFloat("_Exposure", 1.25f);
                AssetDatabase.CreateAsset(skyMat, "Assets/WorldMill/Generated/WorldMillSky.mat");
            }

            // 3. Global Post-Processing Volume (ACES Tonemapping + Bloom + Vignette)
            var volProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            var tonemap = volProfile.Add<Tonemapping>(true);
            tonemap.mode.Override(TonemappingMode.ACES);

            var bloom = volProfile.Add<Bloom>(true);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.45f);
            bloom.scatter.Override(0.72f);

            var colorAdj = volProfile.Add<ColorAdjustments>(true);
            colorAdj.postExposure.Override(0.12f);
            colorAdj.contrast.Override(12f);
            colorAdj.saturation.Override(10f);

            var vignette = volProfile.Add<Vignette>(true);
            vignette.intensity.Override(0.18f);

            AssetDatabase.CreateAsset(volProfile, "Assets/WorldMill/Generated/WorldMillPostVolume.asset");

            // 4. Build New Scene
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (skyMat != null) RenderSettings.skybox = skyMat;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0036f;
            RenderSettings.fogColor = new Color(0.64f, 0.78f, 0.92f);

            // Directional Sun with Cascaded Soft Shadows
            var sunGo = new GameObject("Directional Sun");
            var sunLight = sunGo.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.shadows = LightShadows.Soft;
            sunLight.shadowStrength = 0.88f;
            sunLight.intensity = 2.6f;
            sunLight.color = new Color(1.0f, 0.95f, 0.86f);
            sunGo.transform.rotation = Quaternion.Euler(38f, -15f, 0f);
            RenderSettings.sun = sunLight;

            // Global Post-Processing Volume
            var volGo = new GameObject("Global Post Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = volProfile;

            // World-Following Reflective Water Plane at y = -6.0
            var waterGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            waterGo.name = "WorldMill Water Plane";
            waterGo.transform.position = new Vector3(0f, -6.0f, 0f);
            waterGo.transform.localScale = new Vector3(110f, 1f, 110f);
            Object.DestroyImmediate(waterGo.GetComponent<Collider>());
            waterGo.GetComponent<MeshRenderer>().sharedMaterial = waterMat;

            // WorldMill Streamer
            var streamerGo = new GameObject("WorldMill Streamer");
            var streamer = streamerGo.AddComponent<WorldMillStreamer>();
            streamer.terrainMaterial = terrainMat;

            // Main Camera with URP Post-Processing & Fly Controller
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.position = new Vector3(12f, 36f, -48f);
            camGo.transform.rotation = Quaternion.Euler(14f, 0f, 0f);

            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 650f;
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            var fly = camGo.AddComponent<WorldMillFlyCamera>();
            fly.streamer = streamer;
            fly.sunLight = sunLight;
            fly.waterPlane = waterGo.transform;

            AssetDatabase.SaveAssets();
            string scenePath = "Assets/Scenes/WorldMill.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
            Debug.Log("[WorldMill] Built and saved URP scene successfully at " + scenePath);
        }
    }
}
