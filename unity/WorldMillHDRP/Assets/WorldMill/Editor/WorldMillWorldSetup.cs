using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using WorldMill.UI;

namespace WorldMill.Editor
{
    /// <summary>
    /// Converts all downloaded CC0 KayKit Medieval, Dungeon, Nature & NPC FBX models to
    /// HDRP/Lit materials, builds Idle AnimatorControllers for the 3 Quest-Giver NPCs,
    /// equips sculpted trees/rocks onto WorldMillFoliageSystem, and populates WorldMillWorldBuilder
    /// in Assets/Scenes/WorldMillHDRP.unity.
    /// </summary>
    public static class WorldMillWorldSetup
    {
        private const string MedDir = "Assets/WorldMill/Environment/Medieval";
        private const string DngDir = "Assets/WorldMill/Environment/Dungeon";
        private const string ChrDir = "Assets/WorldMill/Characters";
        private const string GenPropsDir = "Assets/WorldMill/Generated/Props";

        [MenuItem("WorldMill/Build Living World POIs & NPCs")]
        public static void SetupWorldbuildingInScene()
        {
            Directory.CreateDirectory(GenPropsDir);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var lit = Shader.Find("HDRP/Lit");
            if (lit == null)
            {
                Debug.LogError("[WorldMill] HDRP/Lit shader not found.");
                return;
            }

            // 1. Create HDRP/Lit materials for Medieval pack, Dungeon pack, and 3 NPCs
            var medMat = CreateHdrpTexMat(lit, $"{MedDir}/hexagons_medieval.png", "Assets/WorldMill/Generated/MedievalHDRP.mat", 0.06f, 0.28f, true);
            var dngMat = CreateHdrpTexMat(lit, $"{DngDir}/dungeon_texture.png",   "Assets/WorldMill/Generated/DungeonHDRP.mat",  0.14f, 0.36f, false);

            var barbMat  = CreateHdrpTexMat(lit, $"{ChrDir}/barbarian_texture.png", "Assets/WorldMill/Generated/BarbarianHDRP.mat", 0.20f, 0.42f, false);
            var rogueMat = CreateHdrpTexMat(lit, $"{ChrDir}/rogue_texture.png",     "Assets/WorldMill/Generated/RogueHDRP.mat",     0.18f, 0.40f, false);
            var mageMat  = CreateHdrpTexMat(lit, $"{ChrDir}/mage_texture.png",      "Assets/WorldMill/Generated/MageHDRP.mat",      0.16f, 0.46f, false);

            // 2. Build HDRP prefabs for all Medieval buildings & Dungeon props
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

            // 3. Build 3 Rigged Animated Quest-Giver NPC prefabs (looped Idle animation!)
            var barbNpc  = BuildNpcPrefab($"{ChrDir}/Barbarian.fbx",   "NPC_Barbarian", barbMat);
            var rogueNpc = BuildNpcPrefab($"{ChrDir}/RogueHooded.fbx", "NPC_Rogue",     rogueMat);
            var mageNpc  = BuildNpcPrefab($"{ChrDir}/Mage.fbx",        "NPC_Mage",      mageMat);

            // 4. Extract sculpted Tree & Rock meshes for GPU-instanced WorldMillFoliageSystem
            var treeMeshA = LoadFirstMesh($"{MedDir}/tree_single_A.fbx");
            var treeMeshB = LoadFirstMesh($"{MedDir}/tree_single_B.fbx");
            var rockMesh  = LoadFirstMesh($"{MedDir}/rock_single_A.fbx");

            // 5. Wire everything into WorldMillHDRP.unity
            var scene = EditorSceneManager.OpenScene(WorldMillHdrpSetup.ScenePath, OpenSceneMode.Single);

            var foliage = Object.FindFirstObjectByType<WorldMillFoliageSystem>();
            if (foliage != null)
            {
                foliage.sculptedTreeMeshA = treeMeshA;
                foliage.sculptedTreeMeshB = treeMeshB;
                foliage.sculptedRockMesh = rockMesh;
                foliage.sculptedNatureMaterial = medMat;
                if (treeMeshA != null && treeMeshA.bounds.size.y > 0.05f)
                {
                    // Auto-normalize KayKit tree height to ~7.5m human-scale pines!
                    foliage.sculptedTreeScale = 7.5f / treeMeshA.bounds.size.y;
                }
                if (rockMesh != null && rockMesh.bounds.size.y > 0.05f)
                {
                    foliage.sculptedRockScale = 1.6f / rockMesh.bounds.size.y;
                }
                EditorUtility.SetDirty(foliage);
            }

            var builder = Object.FindFirstObjectByType<WorldMillWorldBuilder>();
            if (builder == null)
            {
                var go = new GameObject("WorldMill WorldBuilder (POIs & NPCs)");
                builder = go.AddComponent<WorldMillWorldBuilder>();
            }
            builder.tavernPrefab = tavern;
            builder.blacksmithPrefab = blacksmith;
            builder.homeAPrefab = homeA;
            builder.homeBPrefab = homeB;
            builder.towerPrefab = tower;
            builder.windmillPrefab = windmill;
            builder.wellPrefab = well;
            builder.marketPrefab = market;
            builder.bridgePrefab = bridge;
            builder.pillarPrefab = pillar;
            builder.archPrefab = arch;
            builder.rubblePrefab = rubble;
            builder.torchPrefab = torch;
            builder.chestPrefab = chest;
            builder.barrelPrefab = barrel;
            builder.cratesPrefab = crates;
            builder.barbarianNpcPrefab = barbNpc;
            builder.rogueNpcPrefab = rogueNpc;
            builder.mageNpcPrefab = mageNpc;
            builder.streamer = Object.FindFirstObjectByType<WorldMillStreamer>();
            builder.rpgClient = Object.FindFirstObjectByType<WorldMillRpgClient>();
            var player = Object.FindFirstObjectByType<WorldMillPlayerController>();
            if (player != null)
            {
                builder.playerTransform = player.transform;
                // Spawn player right on the edge of Oakhaven Crossing looking toward the village & river!
                player.transform.position = new Vector3(12f, 22f, -46f);
                EditorUtility.SetDirty(player);
            }
            EditorUtility.SetDirty(builder);

            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[WorldMill] Built Oakhaven Hamlet, River Bridge, Ancient Rune Sanctuary, 3 Rigged NPCs & Sculpted Trees!");
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
            if (asset == null)
            {
                Debug.LogWarning($"[WorldMill] Missing prop FBX: {fbxPath}");
                return null;
            }
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

        private static Mesh LoadFirstMesh(string fbxPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>().FirstOrDefault();
        }
    }
}
