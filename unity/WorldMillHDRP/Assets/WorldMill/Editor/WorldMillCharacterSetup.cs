using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill.Editor
{
    /// <summary>
    /// Converts the CC0 KayKit Knight FBX + weapons to HDRP/Lit, builds a Unity AnimatorController
    /// from the embedded AnimationClips (Idle, Walk, Run, Jump, Dodge, Sword Slash), and equips
    /// the rigged Knight onto Player Adventurer in WorldMillHDRP.unity.
    /// </summary>
    public static class WorldMillCharacterSetup
    {
        private const string KnightFbxPath = "Assets/WorldMill/Characters/Knight.fbx";
        private const string KnightTexPath = "Assets/WorldMill/Characters/knight_texture.png";
        private const string SwordFbxPath = "Assets/WorldMill/Characters/sword_2handed_color.fbx";
        private const string ShieldFbxPath = "Assets/WorldMill/Characters/shield_round_color.fbx";
        private const string ControllerPath = "Assets/WorldMill/Generated/KnightAnimator.controller";
        private const string KnightMatPath = "Assets/WorldMill/Generated/KnightHDRP.mat";
        private const string KnightPrefabPath = "Assets/WorldMill/Generated/KnightHero.prefab";

        [MenuItem("WorldMill/Setup Rigged Knight Character")]
        public static void SetupKnightInScene()
        {
            Directory.CreateDirectory("Assets/WorldMill/Generated");

            // 1. Ensure texture & FBX assets are imported cleanly
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var knightTex = AssetDatabase.LoadAssetAtPath<Texture2D>(KnightTexPath);
            var litShader = Shader.Find("HDRP/Lit");
            if (litShader == null)
            {
                Debug.LogError("[WorldMill] HDRP/Lit shader not found.");
                return;
            }

            // 2. Create crisp metallic HDRP/Lit Knight Armor Material
            var knightMat = AssetDatabase.LoadAssetAtPath<Material>(KnightMatPath);
            if (knightMat == null)
            {
                knightMat = new Material(litShader) { name = "KnightHDRP" };
                AssetDatabase.CreateAsset(knightMat, KnightMatPath);
            }
            knightMat.SetColor("_BaseColor", Color.white);
            if (knightTex != null) knightMat.SetTexture("_BaseColorMap", knightTex);
            knightMat.SetFloat("_Metallic", 0.35f);
            knightMat.SetFloat("_Smoothness", 0.55f);
            HDMaterial.ValidateMaterial(knightMat);
            EditorUtility.SetDirty(knightMat);

            // 3. Configure FBX clip looping for Idle / Walk / Run clips
            var importer = AssetImporter.GetAtPath(KnightFbxPath) as ModelImporter;
            if (importer != null)
            {
                importer.animationType = ModelImporterAnimationType.Generic;
                var clips = importer.defaultClipAnimations;
                for (int i = 0; i < clips.Length; i++)
                {
                    string lower = clips[i].name.ToLowerInvariant();
                    if (lower.Contains("idle") || lower.Contains("walk") || lower.Contains("running") || lower.Contains("swim"))
                    {
                        clips[i].loopTime = true;
                    }
                }
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
            }

            // 4. Discover embedded AnimationClips inside Knight.fbx
            var allSubAssets = AssetDatabase.LoadAllAssetsAtPath(KnightFbxPath);
            var animClips = allSubAssets
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .ToList();

            Debug.Log($"[WorldMill] Found {animClips.Count} animation clips in Knight.fbx");

            AnimationClip FindClip(params string[] patterns)
            {
                foreach (var p in patterns)
                {
                    var exact = animClips.FirstOrDefault(c => c.name.Equals(p, System.StringComparison.OrdinalIgnoreCase));
                    if (exact != null) return exact;
                }
                foreach (var p in patterns)
                {
                    var partial = animClips.FirstOrDefault(c => c.name.ToLowerInvariant().Contains(p.ToLowerInvariant()));
                    if (partial != null) return partial;
                }
                return animClips.FirstOrDefault();
            }

            var idleClip   = FindClip("Idle", "Idle_B", "2H_Melee_Idle");
            var walkClip   = FindClip("Walking_A", "Walking_B", "Walk");
            var runClip    = FindClip("Running_A", "Running_B", "Run");
            var jumpClip   = FindClip("Jump_Full_Short", "Jump_Start", "Jump");
            var dodgeClip  = FindClip("Dodge_Forward", "Dodge");
            var attackClip = FindClip("2H_Melee_Attack_Slice", "1H_Melee_Attack_Slice_Diagonal", "Attack");

            // 5. Build AnimatorController (Idle <-> Walk <-> Run + Jump/Roll/Attack triggers)
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);

            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("IsSwimming", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Roll", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Attack", AnimatorControllerParameterType.Trigger);

            var sm = ctrl.layers[0].stateMachine;
            var sIdle = sm.AddState("Idle");     sIdle.motion = idleClip;
            var sWalk = sm.AddState("Walk");     sWalk.motion = walkClip ?? idleClip;
            var sRun  = sm.AddState("Run");      sRun.motion  = runClip ?? walkClip ?? idleClip;
            var sJump = sm.AddState("Jump");     sJump.motion = jumpClip ?? idleClip;
            var sRoll = sm.AddState("Roll");     sRoll.motion = dodgeClip ?? runClip ?? idleClip;
            var sAtk  = sm.AddState("Attack");   sAtk.motion  = attackClip ?? idleClip;
            sm.defaultState = sIdle;

            // Locomotion transitions
            var tIdleWalk = sIdle.AddTransition(sWalk);
            tIdleWalk.AddCondition(AnimatorConditionMode.Greater, 0.3f, "Speed");
            tIdleWalk.hasExitTime = false; tIdleWalk.duration = 0.12f;

            var tWalkIdle = sWalk.AddTransition(sIdle);
            tWalkIdle.AddCondition(AnimatorConditionMode.Less, 0.25f, "Speed");
            tWalkIdle.hasExitTime = false; tWalkIdle.duration = 0.14f;

            var tWalkRun = sWalk.AddTransition(sRun);
            tWalkRun.AddCondition(AnimatorConditionMode.Greater, 8.5f, "Speed");
            tWalkRun.hasExitTime = false; tWalkRun.duration = 0.14f;

            var tRunWalk = sRun.AddTransition(sWalk);
            tRunWalk.AddCondition(AnimatorConditionMode.Less, 8.0f, "Speed");
            tRunWalk.hasExitTime = false; tRunWalk.duration = 0.14f;

            // Action triggers from AnyState
            void AddAction(AnimatorState state, string trigger)
            {
                var tr = sm.AddAnyStateTransition(state);
                tr.AddCondition(AnimatorConditionMode.If, 0f, trigger);
                tr.hasExitTime = false;
                tr.duration = 0.08f;
                tr.canTransitionToSelf = false;

                var back = state.AddTransition(sIdle);
                back.hasExitTime = true;
                back.exitTime = 0.88f;
                back.duration = 0.12f;
            }
            AddAction(sJump, "Jump");
            AddAction(sRoll, "Roll");
            AddAction(sAtk,  "Attack");

            EditorUtility.SetDirty(ctrl);

            // 6. Build Hero Prefab with HDRP/Lit material, Animator, Greatsword & Shield
            var knightAsset = AssetDatabase.LoadAssetAtPath<GameObject>(KnightFbxPath);
            if (knightAsset == null)
            {
                Debug.LogError("[WorldMill] Could not load Knight.fbx");
                return;
            }

            var root = (GameObject)PrefabUtility.InstantiatePrefab(knightAsset);
            root.name = "KnightHero";

            // Apply HDRP/Lit Knight material to every SkinnedMeshRenderer & MeshRenderer
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = knightMat;
                r.sharedMaterials = mats;
            }

            var anim = root.GetComponent<Animator>();
            if (anim == null) anim = root.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;

            // Attach 2-Handed Greatsword to right hand slot & Shield to left hand slot
            AttachProp(root.transform, SwordFbxPath, knightMat, new[] { "handslot.r", "hand.r", "RightHand" },
                Vector3.zero, new Vector3(0f, 0f, 0f), 1.0f);
            AttachProp(root.transform, ShieldFbxPath, knightMat, new[] { "handslot.l", "hand.l", "LeftHand" },
                Vector3.zero, new Vector3(0f, 0f, 0f), 1.0f);

            var heroPrefab = PrefabUtility.SaveAsPrefabAsset(root, KnightPrefabPath);
            Object.DestroyImmediate(root);

            // 7. Wire KnightHero prefab onto Player Adventurer in WorldMillHDRP.unity
            var scene = EditorSceneManager.OpenScene(WorldMillHdrpSetup.ScenePath, OpenSceneMode.Single);
            var player = Object.FindFirstObjectByType<WorldMillPlayerController>();
            if (player != null)
            {
                player.characterModelPrefab = heroPrefab;
                EditorUtility.SetDirty(player);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[WorldMill] Equipped rigged KnightHero + AnimatorController onto Player Adventurer!");
            }
            AssetDatabase.SaveAssets();
        }

        private static void AttachProp(Transform root, string fbxPath, Material mat, string[] boneCandidates, Vector3 localPos, Vector3 localEuler, float scale)
        {
            var propAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (propAsset == null) return;

            Transform targetBone = null;
            foreach (var tf in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var name in boneCandidates)
                {
                    if (tf.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        targetBone = tf;
                        break;
                    }
                }
                if (targetBone != null) break;
            }
            if (targetBone == null) return;

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(propAsset);
            inst.transform.SetParent(targetBone, false);
            inst.transform.localPosition = localPos;
            inst.transform.localEulerAngles = localEuler;
            inst.transform.localScale = Vector3.one * scale;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }
    }
}
