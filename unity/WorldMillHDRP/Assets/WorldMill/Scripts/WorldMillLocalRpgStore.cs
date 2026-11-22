using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill
{
    [Serializable]
    public sealed class RpgInventoryItem
    {
        public string id;
        public string name;
        public string kind; // weapon | armor | material | consumable
        public int quantity;
        public string description;
    }

    [Serializable]
    public sealed class RpgLoreEntry
    {
        public string title;
        public string text;
    }

    [Serializable]
    public sealed class RpgQuest
    {
        public string id;
        public string title;
        public string giver;
        public string description;
        public Vector3 targetPosition;
        public string[] objectives;
        public RpgLoreEntry[] lore;
        public int rewardXp;
        public RpgInventoryItem rewardItem;
        public bool completed;
    }

    [Serializable]
    public sealed class PlacedStructure
    {
        public int id;
        public string kind;
        public Vector3 position;
        public float rotationY;
    }

    [Serializable]
    public sealed class LocalSaveData
    {
        public int level = 1;
        public int xp = 0;
        public int xpToNext = 100;
        public float maxHp = 100f;
        public float maxStamina = 100f;
        public string equippedWeapon = "Iron Greatsword";
        public string equippedArmor = "Weathered Brigandine";
        public string activeQuestId = "q_river_crossing";
        public List<string> completedQuests = new();
        public List<RpgInventoryItem> inventory = new();
        public List<PlacedStructure> structures = new();
        public int nextStructureId = 1;
    }

    /// <summary>
    /// Replacement for the deprecated HDAdditionalLightData.SetIntensity(float, LightUnit):
    /// same conversion HDRP performs internally, without the CS0618 warning.
    /// </summary>
    public static class WorldMillLightUtil
    {
        public static void SetLightIntensity(this HDAdditionalLightData hd, float intensity, LightUnit unit)
        {
            if (hd == null) return;
            var light = hd.GetComponent<Light>();
            if (light == null) return;
            light.intensity = LightUnitUtils.ConvertIntensity(light, intensity, unit, LightUnitUtils.GetNativeLightUnit(light.type));
            light.lightUnit = unit;
        }
    }

    /// <summary>
    /// 100% self-contained local RPG state, quests, lore, inventory, and persistent
    /// HDRP campfire/structure store (no external server required).
    /// </summary>
    public sealed class WorldMillLocalRpgStore : MonoBehaviour
    {
        public Transform playerTransform;
        [Tooltip("Auto-resolved. While its menus, journal or title are open, E does not complete quests.")]
        public WorldMill.UI.WorldMillMinimalHud minimalHud;
        public float questCompletionRadius = 16f;

        public LocalSaveData Save { get; private set; }
        public List<RpgQuest> Quests { get; private set; } = new();
        public RpgQuest ActiveQuest { get; private set; }
        public string ToastMessage { get; private set; } = "";
        public float ToastTimestamp { get; private set; } = -10f;
        public bool CanCompleteActiveQuest { get; private set; }
        public float DistanceToActiveQuest { get; private set; } = 999f;

        /// True once the journey differs from a fresh save: XP or levels earned, a quest completed,
        /// a different quest tracked, or a campfire placed. Drives "Continue" on the title screen.
        public bool HasSaveProgress =>
            Save != null && (Save.level > 1 || Save.xp > 0 || Save.completedQuests.Count > 0 ||
                             Save.structures.Count > 0 || Save.activeQuestId != DefaultActiveQuestId);

        private const string DefaultActiveQuestId = "q_river_crossing";

        private string SavePath => Path.Combine(Application.persistentDataPath, "worldmill_rpg_save.json");
        private readonly List<(Light l, HDAdditionalLightData hd, float phase)> _campfireLights = new();
        private readonly List<GameObject> _campfireRoots = new();

        private void Awake()
        {
            BuildQuestCatalog();
            LoadOrCreateSave();
            SyncQuestStatuses();
            RespawnSavedStructures();
        }

        private void Update()
        {
            if (playerTransform == null)
            {
                var p = FindFirstObjectByType<WorldMillPlayerController>();
                if (p != null) playerTransform = p.transform;
            }

            if (minimalHud == null) minimalHud = FindFirstObjectByType<WorldMill.UI.WorldMillMinimalHud>();
            bool inputBlocked = minimalHud != null && minimalHud.IsInputBlocked;

            if (ActiveQuest != null && !ActiveQuest.completed && playerTransform != null)
            {
                DistanceToActiveQuest = Vector3.Distance(playerTransform.position, ActiveQuest.targetPosition);
                CanCompleteActiveQuest = DistanceToActiveQuest <= questCompletionRadius;
                if (CanCompleteActiveQuest && !inputBlocked && Input.GetKeyDown(KeyCode.E))
                {
                    CompleteQuest(ActiveQuest.id);
                }
            }
            else
            {
                CanCompleteActiveQuest = false;
                DistanceToActiveQuest = 999f;
            }

            // Flicker placed campfires
            float t = Time.time;
            for (int i = 0; i < _campfireLights.Count; i++)
            {
                var c = _campfireLights[i];
                if (c.l == null) continue;
                float flick = 0.88f + 0.14f * Mathf.Sin(t * 13.1f + c.phase) + 0.08f * Mathf.Cos(t * 25.7f + c.phase);
                if (c.hd != null) c.hd.SetLightIntensity(1250f * flick, LightUnit.Lumen);
                else c.l.intensity = 1250f * flick;
            }
        }

        public void SetActiveQuest(string questId)
        {
            var q = Quests.Find(x => x.id == questId);
            if (q == null) return;
            Save.activeQuestId = q.id;
            ActiveQuest = q;
            Persist();
            ShowToast($"Tracking: {q.title}");
        }

        public void CompleteQuest(string questId)
        {
            var q = Quests.Find(x => x.id == questId);
            if (q == null || q.completed) return;

            q.completed = true;
            if (!Save.completedQuests.Contains(q.id))
                Save.completedQuests.Add(q.id);

            Save.xp += q.rewardXp;
            int levelsGained = 0;
            while (Save.xp >= Save.xpToNext)
            {
                Save.xp -= Save.xpToNext;
                Save.level++;
                levelsGained++;
                Save.xpToNext = 100 + 50 * (Save.level - 1);
                Save.maxHp += 15f;
                Save.maxStamina += 10f;
            }

            if (q.rewardItem != null)
            {
                var existing = Save.inventory.Find(i => i.id == q.rewardItem.id);
                if (existing != null) existing.quantity += q.rewardItem.quantity;
                else Save.inventory.Add(new RpgInventoryItem
                {
                    id = q.rewardItem.id,
                    name = q.rewardItem.name,
                    kind = q.rewardItem.kind,
                    quantity = q.rewardItem.quantity,
                    description = q.rewardItem.description
                });
                if (q.rewardItem.kind == "weapon")
                    Save.equippedWeapon = q.rewardItem.name;
            }

            ActiveQuest = Quests.Find(x => !x.completed);
            Save.activeQuestId = ActiveQuest != null ? ActiveQuest.id : "";
            Persist();

            string lvlTxt = levelsGained > 0 ? $" · Level {Save.level}!" : "";
            ShowToast($"Quest completed: {q.title} (+{q.rewardXp} XP{lvlTxt})");
        }

        public void PlaceCampfireAt(Vector3 pos, float yawDeg)
        {
            if (Terrain.activeTerrain != null)
            {
                pos.y = Terrain.activeTerrain.SampleHeight(pos) + Terrain.activeTerrain.transform.position.y;
            }
            var s = new PlacedStructure
            {
                id = Save.nextStructureId++,
                kind = "campfire",
                position = pos,
                rotationY = yawDeg
            };
            Save.structures.Add(s);
            Persist();
            SpawnCampfireVisual(s);
            ShowToast("Campfire lit");
        }

        /// Writes the save to disk immediately (progress is also saved on every quest / campfire).
        public void SaveNow(bool toast = true)
        {
            if (Save == null) return;
            bool ok = Persist();
            if (toast) ShowToast(ok ? "Journey saved" : "Could not save the journey");
        }

        /// Starts a fresh journey: removes placed campfires, resets the save to defaults,
        /// re-syncs quest state and persists. The player controller handles respawn.
        public void ResetSave(bool toast = true)
        {
            foreach (var root in _campfireRoots)
                if (root != null) Destroy(root);
            _campfireRoots.Clear();
            _campfireLights.Clear();

            Save = CreateFreshSave();
            SyncQuestStatuses();
            CanCompleteActiveQuest = false;
            DistanceToActiveQuest = 999f;
            Persist();
            if (toast) ShowToast("A new journey begins");
        }

        public void ShowToast(string msg)
        {
            ToastMessage = msg;
            ToastTimestamp = Time.unscaledTime;
        }

        private void BuildQuestCatalog()
        {
            Quests = new List<RpgQuest>
            {
                new RpgQuest
                {
                    id = "q_river_crossing",
                    title = "The Sunken Crossing",
                    giver = "Aldric the Ferryman",
                    description = "Follow the dirt road west out of Oakhaven to the flooded river crossing and speak with Aldric by the bridge.",
                    targetPosition = new Vector3(448f, 4f, 492f),
                    objectives = new[] { "Follow the road west to the river bridge", "Inspect the flooded crossing" },
                    lore = new[]
                    {
                        new RpgLoreEntry
                        {
                            title = "Flooded King's Road",
                            text = "Three winters ago the northern glaciers fractured, swelling the valley river until it drowned the old merchant ford. Only the timber bridge west of Oakhaven still stands."
                        }
                    },
                    rewardXp = 150,
                    rewardItem = new RpgInventoryItem { id = "ferrymans_lantern", name = "Ferryman's Lantern", kind = "trinket", quantity = 1, description = "Warm oil lantern etched with river wards." }
                },
                new RpgQuest
                {
                    id = "q_rune_sanctuary",
                    title = "Watch of the High Stones",
                    giver = "Runekeeper Astrid",
                    description = "Climb the ridge road northeast of Oakhaven to the Ancient Rune Sanctuary overlooking the valley.",
                    targetPosition = new Vector3(618f, 38f, 588f),
                    objectives = new[] { "Ascend the northeastern ridge", "Reach the Ancient Rune Sanctuary" },
                    lore = new[]
                    {
                        new RpgLoreEntry
                        {
                            title = "The Standing Circle",
                            text = "Carved long before Oakhaven's first timber was felled, the six granite pillars align with the northern peaks to mark the ancient border of the highland clans."
                        }
                    },
                    rewardXp = 300,
                    rewardItem = new RpgInventoryItem { id = "runic_cloak", name = "Astrid's Runic Mantle", kind = "armor", quantity = 1, description = "Heavy wool mantle threaded with pale silver wards." }
                },
                new RpgQuest
                {
                    id = "q_alpine_summit",
                    title = "Crown of the Northern Ridge",
                    giver = "Hearth-Smith Torin",
                    description = "Venture into the high snow peaks north of the valley to claim the forged Heartcrystal relic.",
                    targetPosition = new Vector3(540f, 76f, 760f),
                    objectives = new[] { "Cross the northern pine forests", "Reach the snowline summit" },
                    lore = new[]
                    {
                        new RpgLoreEntry
                        {
                            title = "Heartcrystal Steel",
                            text = "Smiths of the First Hearth quenched their blades in glacial meltwater mixed with crushed blue quartz from the northern peaks."
                        }
                    },
                    rewardXp = 500,
                    rewardItem = new RpgInventoryItem { id = "heartcrystal_blade", name = "Heartcrystal Blade", kind = "weapon", quantity = 1, description = "Masterwork greatsword with a pale crystalline fuller." }
                }
            };
        }

        private void LoadOrCreateSave()
        {
            if (File.Exists(SavePath))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<LocalSaveData>(File.ReadAllText(SavePath));
                    if (loaded != null && loaded.level >= 1)
                    {
                        Save = loaded;
                        return;
                    }
                }
                catch { /* fall through to fresh save */ }
            }

            Save = CreateFreshSave();
            Persist();
        }

        private static LocalSaveData CreateFreshSave()
        {
            var save = new LocalSaveData { activeQuestId = DefaultActiveQuestId };
            save.inventory.Add(new RpgInventoryItem { id = "iron_greatsword", name = "Iron Greatsword", kind = "weapon", quantity = 1, description = "Balanced northern longblade." });
            save.inventory.Add(new RpgInventoryItem { id = "weathered_brigandine", name = "Weathered Brigandine", kind = "armor", quantity = 1, description = "Riveted leather and iron plates." });
            save.inventory.Add(new RpgInventoryItem { id = "pine_torch", name = "Pitch Pine Torch", kind = "consumable", quantity = 3, description = "Press T to ignite in dusk or deep woods." });
            return save;
        }

        private void SyncQuestStatuses()
        {
            foreach (var q in Quests)
            {
                if (Terrain.activeTerrain != null)
                {
                    float y = Terrain.activeTerrain.SampleHeight(q.targetPosition) + Terrain.activeTerrain.transform.position.y;
                    q.targetPosition = new Vector3(q.targetPosition.x, y + 1.2f, q.targetPosition.z);
                }
                q.completed = Save.completedQuests.Contains(q.id);
            }
            ActiveQuest = Quests.Find(x => x.id == Save.activeQuestId && !x.completed) ?? Quests.Find(x => !x.completed);
        }

        private void RespawnSavedStructures()
        {
            foreach (var s in Save.structures)
            {
                if (s.kind == "campfire")
                    SpawnCampfireVisual(s);
            }
        }

        private void SpawnCampfireVisual(PlacedStructure s)
        {
            var lit = Shader.Find("HDRP/Lit");
            if (lit == null) return;

            var root = new GameObject($"Campfire_{s.id}");
            _campfireRoots.Add(root);
            root.transform.position = s.position;
            root.transform.rotation = Quaternion.Euler(0f, s.rotationY, 0f);

            Material MakeMat(Color baseCol, float smooth, Color emissive, float emInt)
            {
                var m = new Material(lit);
                m.SetColor("_BaseColor", baseCol);
                m.SetFloat("_Smoothness", smooth);
                if (emInt > 0f)
                {
                    m.SetColor("_EmissiveColor", emissive);
                    m.SetColor("_EmissiveColorLDR", emissive);
                    m.SetFloat("_UseEmissiveIntensity", 1f);
                    m.SetFloat("_EmissiveIntensity", emInt);
                }
                HDMaterial.ValidateMaterial(m);
                return m;
            }

            var stoneMat = MakeMat(new Color(0.24f, 0.24f, 0.26f), 0.2f, Color.black, 0f);
            var woodMat  = MakeMat(new Color(0.14f, 0.09f, 0.06f), 0.1f, Color.black, 0f);
            var emberMat = MakeMat(new Color(1.0f, 0.42f, 0.08f), 0.3f, new Color(1.0f, 0.45f, 0.08f), 14f);

            // Ring of 8 stones
            for (int i = 0; i < 8; i++)
            {
                float a = (i / 8f) * Mathf.PI * 2f;
                var st = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(st.GetComponent<Collider>());
                st.transform.SetParent(root.transform, false);
                st.transform.localPosition = new Vector3(Mathf.Cos(a) * 0.62f, 0.10f, Mathf.Sin(a) * 0.62f);
                st.transform.localScale = new Vector3(0.32f, 0.20f, 0.28f);
                st.GetComponent<MeshRenderer>().sharedMaterial = stoneMat;
            }
            // Crossed logs
            for (int i = 0; i < 3; i++)
            {
                var log = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(log.GetComponent<Collider>());
                log.transform.SetParent(root.transform, false);
                log.transform.localPosition = new Vector3(0f, 0.14f, 0f);
                log.transform.localScale = new Vector3(0.14f, 0.50f, 0.14f);
                log.transform.localRotation = Quaternion.Euler(78f, i * 60f, 0f);
                log.GetComponent<MeshRenderer>().sharedMaterial = woodMat;
            }
            // Glowing ember core
            var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = new Vector3(0f, 0.22f, 0f);
            core.transform.localScale = new Vector3(0.42f, 0.30f, 0.42f);
            core.GetComponent<MeshRenderer>().sharedMaterial = emberMat;

            var lgo = new GameObject("CampfireLight");
            lgo.transform.SetParent(root.transform, false);
            lgo.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1.0f, 0.56f, 0.20f);
            l.range = 20f;
            var hd = lgo.AddComponent<HDAdditionalLightData>();
            hd.SetLightIntensity(1250f, LightUnit.Lumen);
            hd.EnableShadows(true);
            _campfireLights.Add((l, hd, s.id * 1.37f));
        }

        private bool Persist()
        {
            try { File.WriteAllText(SavePath, JsonUtility.ToJson(Save, true)); return true; }
            catch (Exception e) { Debug.LogWarning($"[WorldMill] Failed to save RPG state: {e.Message}"); return false; }
        }
    }
}
