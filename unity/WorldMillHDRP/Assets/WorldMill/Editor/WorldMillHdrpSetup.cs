using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using WorldMill.UI;

namespace WorldMill.Editor
{
    /// <summary>
    /// Builds the complete, 100% self-contained Unity 6 HDRP Open-World RPG scene:
    /// - 1024m x 1024m Hydraulic + Thermal Eroded native HDRP/TerrainLit Terrain
    /// - Carved meandering river filled by HDRP Infinite FFT Ocean (y = 0)
    /// - Winding dirt roads connecting Oakhaven Hamlet -> River Bridge -> Ancient Rune Sanctuary
    /// - Native Unity Terrain sculpted pine forests (auto-spaced, clear of roads & buildings)
    /// - Oakhaven Crossing Hamlet, Sunken River Bridge, Ancient Rune Sanctuary, Summit Shrine
    /// - Rigged Armored Knight hero (76 clips) + 3 Rigged Animated Quest-Giver NPCs
    /// - Physically Based Sky + Volumetric Clouds + Volumetric Fog + SSAO + SSR + ACES Bloom
    /// - Local RPG persistence + Opus's restrained Witcher/Elden Ring minimal HUD
    /// </summary>
    public static class WorldMillHdrpSetup
    {
        public const string ScenePath = "Assets/Scenes/WorldMillHDRP.unity";
        private const string MedDir = "Assets/WorldMill/Environment/Medieval";
        private const string DngDir = "Assets/WorldMill/Environment/Dungeon";
        private const string ChrDir = "Assets/WorldMill/Characters";
        private const string GenPropsDir = "Assets/WorldMill/Generated/Props";

        [MenuItem("WorldMill/Build Complete Eroded HDRP RPG World")]
        public static void BuildScene()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneLinux64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });

            Directory.CreateDirectory("Assets/WorldMill/Generated");
            Directory.CreateDirectory("Assets/WorldMill/Textures");
            Directory.CreateDirectory(GenPropsDir);
            Directory.CreateDirectory("Assets/Scenes");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 1. HDRP Pipeline Asset (Water, Volumetric Clouds, Volumetric Fog, SSAO, SSR, Contact Shadows)
            var hdrpAsset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>("Assets/WorldMill/Generated/WorldMillHDRPAsset.asset");
            if (hdrpAsset == null)
            {
                hdrpAsset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                AssetDatabase.CreateAsset(hdrpAsset, "Assets/WorldMill/Generated/WorldMillHDRPAsset.asset");
            }
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

            // 2. Seamless 512x512 PBR Texture Sets for TerrainLayers
            var texDir = "Assets/WorldMill/Textures";
            var granite   = WorldMillPbrTextureGen.GenerateLayer(texDir, "L0_Granite", 0);
            var meadow    = WorldMillPbrTextureGen.GenerateLayer(texDir, "L1_Meadow", 1);
            var sandstone = WorldMillPbrTextureGen.GenerateLayer(texDir, "L2_Sandstone", 2);
            var snow      = WorldMillPbrTextureGen.GenerateLayer(texDir, "L3_Snow", 3);

            // 3. HDRP/Lit materials & prefabs for Medieval buildings, Dungeon ruins, Sculpted Trees & NPCs
            var lit = Shader.Find("HDRP/Lit");
            var medMat   = CreateHdrpTexMat(lit, $"{MedDir}/hexagons_medieval.png", "Assets/WorldMill/Generated/MedievalHDRP.mat", 0.06f, 0.28f, true);
            var dngMat   = CreateHdrpTexMat(lit, $"{DngDir}/dungeon_texture.png",   "Assets/WorldMill/Generated/DungeonHDRP.mat",  0.14f, 0.36f, false);
            var barbMat  = CreateHdrpTexMat(lit, $"{ChrDir}/barbarian_texture.png", "Assets/WorldMill/Generated/BarbarianHDRP.mat", 0.20f, 0.42f, false);
            var rogueMat = CreateHdrpTexMat(lit, $"{ChrDir}/rogue_texture.png",     "Assets/WorldMill/Generated/RogueHDRP.mat",     0.18f, 0.40f, false);
            var mageMat  = CreateHdrpTexMat(lit, $"{ChrDir}/mage_texture.png",      "Assets/WorldMill/Generated/MageHDRP.mat",      0.16f, 0.46f, false);

            var treePrefabA = BuildPropPrefab($"{MedDir}/tree_single_A.fbx", "TreePineA", medMat);
            var treePrefabB = BuildPropPrefab($"{MedDir}/tree_single_B.fbx", "TreePineB", medMat);

            var tavern     = BuildPropPrefab($"{MedDir}/building_tavern_red.fbx",     "Tavern",     medMat);
            var blacksmith = BuildPropPrefab($"{MedDir}/building_blacksmith_red.fbx", "Blacksmith", medMat);
            var homeA      = BuildPropPrefab($"{MedDir}/building_home_A_red.fbx",     "HomeA",      medMat);
            var homeB      = BuildPropPrefab($"{MedDir}/building_home_B_red.fbx",     "HomeB",      medMat);
            var tower      = BuildPropPrefab($"{MedDir}/building_tower_A_red.fbx",    "Tower",      medMat);
            var windmill   = BuildPropPrefab($"{MedDir}/building_windmill_red.fbx",   "Windmill",   medMat);
            var well       = BuildPropPrefab($"{MedDir}/building_well_red.fbx",       "Well",       medMat);
            var market     = BuildPropPrefab($"{MedDir}/building_market_red.fbx",     "Market",     medMat);
            var bridge     = BuildPropPrefab($"{MedDir}/building_bridge_A.fbx",       "Bridge",     medMat);

            var pillar     = BuildPropPrefab($"{DngDir}/pillar_decorated.fbx",        "Pillar",     dngMat);
            var arch       = BuildPropPrefab($"{DngDir}/wall_arched.fbx",             "Arch",       dngMat);
            var rubble     = BuildPropPrefab($"{DngDir}/rubble_large.fbx",            "Rubble",     dngMat);
            var torch      = BuildPropPrefab($"{DngDir}/torch_lit.fbx",               "TorchLit",   dngMat);
            var chest      = BuildPropPrefab($"{DngDir}/chest_gold.fbx",              "ChestGold",  dngMat);
            var barrel     = BuildPropPrefab($"{DngDir}/barrel_large_decorated.fbx",  "Barrel",     dngMat);
            var crates     = BuildPropPrefab($"{DngDir}/crates_stacked.fbx",          "Crates",     dngMat);

            var barbNpc    = BuildNpcPrefab($"{ChrDir}/Barbarian.fbx",   "NPC_Barbarian", barbMat);
            var rogueNpc   = BuildNpcPrefab($"{ChrDir}/RogueHooded.fbx", "NPC_Rogue",     rogueMat);
            var mageNpc    = BuildNpcPrefab($"{ChrDir}/Mage.fbx",        "NPC_Mage",      mageMat);

            // 4. Global HDRP Sky, Volumetric Clouds, Volumetric Fog, Water, SSAO, SSR, Exposure & ACES Volume
            string volPath = "Assets/WorldMill/Generated/WorldMillHDRPVolume.asset";
            if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(volPath) != null)
                AssetDatabase.DeleteAsset(volPath);
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
            fog.meanFreePath.Override(420f);
            fog.baseHeight.Override(-4f);
            fog.maximumHeight.Override(160f);

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
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.28f);
            bloom.scatter.Override(0.68f);

            AssetDatabase.CreateAsset(volProfile, volPath);

            // 5. Build Scene
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Eroded 1024m x 1024m HDRP Terrain with carved river, dirt roads & native pine forests!
            var terrain = WorldMillTerrainGenerator.BuildErodedTerrain(granite, meadow, sandstone, snow, treePrefabA, treePrefabB);

            // Global HDRP Sky & Post-Processing Volume
            var volGo = new GameObject("HDRP Sky & Atmosphere Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = volProfile;

            // Physical Directional Sun
            var sunGo = new GameObject("Physical Directional Sun");
            var sunLight = sunGo.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.useColorTemperature = true;
            sunLight.colorTemperature = 5300f;
            var hdSun = sunGo.AddComponent<HDAdditionalLightData>();
            hdSun.SetIntensity(95000f, LightUnit.Lux);
            hdSun.EnableShadows(true);
            hdSun.SetShadowResolutionLevel(2);
            sunGo.transform.rotation = Quaternion.Euler(26f, -22f, 0f);
            RenderSettings.sun = sunLight;

            // Native Unity 6 HDRP Infinite FFT Ocean at world y = 0.0 (fills fjord + carved river!)
            var waterGo = new GameObject("HDRP Infinite Ocean & River");
            waterGo.transform.position = new Vector3(512f, 0.0f, 512f);
            var water = waterGo.AddComponent<WaterSurface>();
            water.surfaceType = WaterSurfaceType.OceanSeaLake;
            water.geometryType = WaterGeometryType.Infinite;

            // Local Self-Contained RPG Store & Opus's Restrained Minimal HUD
            var rpgGo = new GameObject("WorldMill RPG Systems");
            var rpgStore = rpgGo.AddComponent<WorldMillLocalRpgStore>();
            var minimalHud = rpgGo.AddComponent<WorldMillMinimalHud>();
            minimalHud.rpgStore = rpgStore;

            // Main HDRP Camera (TAA)
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1200f;
            var hdCam = camGo.AddComponent<HDAdditionalCameraData>();
            hdCam.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;

            // Third-Person Rigged Armored Knight Player spawned on the Oakhaven approach road!
            float spawnY = terrain.SampleHeight(new Vector3(512f, 0f, 438f)) + terrain.transform.position.y + 0.1f;
            var playerGo = new GameObject("Player Adventurer");
            playerGo.transform.position = new Vector3(512f, spawnY, 438f);
            var player = playerGo.AddComponent<WorldMillPlayerController>();
            player.sunLight = sunLight;
            player.playerCamera = cam;
            player.rpgStore = rpgStore;
            player.minimalHud = minimalHud;
            player.waterLevel = 0.0f;

            var knightPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/WorldMill/Generated/KnightHero.prefab");
            if (knightPrefab != null)
                player.characterModelPrefab = knightPrefab;

            rpgStore.playerTransform = playerGo.transform;
            minimalHud.playerTransform = playerGo.transform;

            // Worldbuilding Landmarks & 3 Animated Rigged Quest-Giver NPCs
            var wbGo = new GameObject("WorldMill WorldBuilder (POIs & NPCs)");
            var wb = wbGo.AddComponent<WorldMillWorldBuilder>();
            wb.tavernPrefab = tavern;
            wb.blacksmithPrefab = blacksmith;
            wb.homeAPrefab = homeA;
            wb.homeBPrefab = homeB;
            wb.towerPrefab = tower;
            wb.windmillPrefab = windmill;
            wb.wellPrefab = well;
            wb.marketPrefab = market;
            wb.bridgePrefab = bridge;
            wb.pillarPrefab = pillar;
            wb.archPrefab = arch;
            wb.rubblePrefab = rubble;
            wb.torchPrefab = torch;
            wb.chestPrefab = chest;
            wb.barrelPrefab = barrel;
            wb.cratesPrefab = crates;
            wb.barbarianNpcPrefab = barbNpc;
            wb.rogueNpcPrefab = rogueNpc;
            wb.mageNpcPrefab = mageNpc;
            wb.rpgStore = rpgStore;
            wb.playerTransform = playerGo.transform;

            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[WorldMill HDRP] Built complete eroded native HDRP open-world RPG scene at " + ScenePath);
        }

        private static Material CreateHdrpTexMat(Shader lit, string texPath, string matPath, float metallic, float smoothness, bool instancing)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(lit) { name = Path.GetFileNameWithoutExtension(matPath) };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.enableInstancing = instancing;
            mat.SetColor("_BaseColor", Color.white);
            if (tex != null) mat.SetTexture("_BaseColorMap", tex);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static GameObject BuildPropPrefab(string fbxPath, string name, Material mat)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (asset == null) return null;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            inst.name = name;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(inst, $"{GenPropsDir}/{name}.prefab");
            Object.DestroyImmediate(inst);
            return prefab;
        }

        private static GameObject BuildNpcPrefab(string fbxPath, string name, Material mat)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp != null)
            {
                imp.animationType = ModelImporterAnimationType.Generic;
                var clips = imp.defaultClipAnimations;
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i].name.ToLowerInvariant().Contains("idle"))
                        clips[i].loopTime = true;
                }
                imp.clipAnimations = clips;
                imp.SaveAndReimport();
            }

            var idleClip = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__") && c.name.ToLowerInvariant().Contains("idle"));

            string ctrlPath = $"{GenPropsDir}/{name}_Idle.controller";
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath) != null)
                AssetDatabase.DeleteAsset(ctrlPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var st = ctrl.layers[0].stateMachine.AddState("Idle");
            st.motion = idleClip;
            ctrl.layers[0].stateMachine.defaultState = st;
            EditorUtility.SetDirty(ctrl);

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (asset == null) return null;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            inst.name = name;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
            var anim = inst.GetComponent<Animator>();
            if (anim == null) anim = inst.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;

            var prefab = PrefabUtility.SaveAsPrefabAsset(inst, $"{GenPropsDir}/{name}.prefab");
            Object.DestroyImmediate(inst);
            return prefab;
        }

        private static void SetBool(SerializedObject so, string propPath, bool value)
        {
            var prop = so.FindProperty(propPath);
            if (prop != null) prop.boolValue = value;
        }
    }
}
