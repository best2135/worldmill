using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill
{
    /// <summary>
    /// Places handcrafted medieval RPG landmarks directly onto the eroded HDRP Terrain:
    /// - Oakhaven Crossing Hamlet (on flat plateau at 512, 460)
    /// - The Sunken Crossing River Bridge (spanning the carved river at 448, 492)
    /// - Ancient Rune Sanctuary (on cliffside overlook at 618, 588)
    /// - Northern Summit Cairn & Chest (at 540, 760)
    /// Zero network wait — every building, torch, and rigged NPC grounds cleanly on frame 1!
    /// </summary>
    public sealed class WorldMillWorldBuilder : MonoBehaviour
    {
        [Header("Medieval Building Prefabs")]
        public GameObject tavernPrefab;
        public GameObject blacksmithPrefab;
        public GameObject homeAPrefab;
        public GameObject homeBPrefab;
        public GameObject towerPrefab;
        public GameObject windmillPrefab;
        public GameObject wellPrefab;
        public GameObject marketPrefab;
        public GameObject bridgePrefab;

        [Header("Ancient Ruin & Camp Prop Prefabs")]
        public GameObject pillarPrefab;
        public GameObject archPrefab;
        public GameObject rubblePrefab;
        public GameObject torchPrefab;
        public GameObject chestPrefab;
        public GameObject barrelPrefab;
        public GameObject cratesPrefab;

        [Header("Rigged Quest-Giver NPC Prefabs")]
        public GameObject barbarianNpcPrefab;
        public GameObject rogueNpcPrefab;
        public GameObject mageNpcPrefab;

        [Header("Scene Links")]
        public WorldMillLocalRpgStore rpgStore;
        public Transform playerTransform;

        private readonly List<(Light light, HDAdditionalLightData hd, float baseLum, float phase)> _flickerLights = new();

        private void Start()
        {
            BuildOakhavenHamlet(new Vector2(512f, 460f));
            BuildRiverBridgeOutpost(new Vector2(448f, 492f));
            BuildAncientRuneSanctuary(new Vector2(618f, 588f));
            BuildNorthernSummitShrine(new Vector2(540f, 760f));
        }

        private void Update()
        {
            float t = Time.time;
            for (int i = 0; i < _flickerLights.Count; i++)
            {
                var f = _flickerLights[i];
                if (f.light == null) continue;
                float flick = 0.88f + 0.14f * Mathf.Sin(t * 11.7f + f.phase) + 0.07f * Mathf.Cos(t * 23.3f + f.phase * 1.7f);
                if (f.hd != null) f.hd.SetIntensity(f.baseLum * flick, LightUnit.Lumen);
                else f.light.intensity = f.baseLum * flick;
            }
        }

        private float SampleGroundY(float x, float z)
        {
            var t = Terrain.activeTerrain;
            if (t == null) return 0f;
            return t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y;
        }

        private void BuildOakhavenHamlet(Vector2 c)
        {
            SpawnProp(tavernPrefab,     "Oakhaven_Tavern",     c + new Vector2(-11f,  7f), 145f, 5.2f, 0.35f, true);
            SpawnProp(blacksmithPrefab, "Oakhaven_Smithy",     c + new Vector2( 12f,  6f), 220f, 5.0f, 0.35f, true);
            SpawnProp(homeAPrefab,      "Oakhaven_CottageA",   c + new Vector2(-13f, -9f),  45f, 4.8f, 0.35f, true);
            SpawnProp(homeBPrefab,      "Oakhaven_CottageB",   c + new Vector2( 13f, -10f), -55f, 4.8f, 0.35f, true);
            SpawnProp(marketPrefab,     "Oakhaven_Market",     c + new Vector2(  1f, 12f), 185f, 4.8f, 0.30f, true);
            SpawnProp(wellPrefab,       "Oakhaven_Well",       c + new Vector2(  0f,  0f),  20f, 4.2f, 0.20f, true);
            SpawnProp(towerPrefab,      "Oakhaven_Watchtower", c + new Vector2( 22f, 16f), 210f, 5.8f, 0.55f, true);
            SpawnProp(windmillPrefab,   "Oakhaven_Windmill",   c + new Vector2(-24f, 19f), 120f, 5.6f, 0.55f, true);

            SpawnProp(barrelPrefab, "Oakhaven_Barrels1", c + new Vector2(-5f,  3f), 30f, 1.3f, 0.05f, true);
            SpawnProp(cratesPrefab, "Oakhaven_Crates1",  c + new Vector2( 6f,  2f), 75f, 1.3f, 0.05f, true);
            SpawnProp(cratesPrefab, "Oakhaven_Crates2",  c + new Vector2(-6f, -5f), 15f, 1.2f, 0.05f, true);

            SpawnTorch(c + new Vector2(-4f,  4f), 950f);
            SpawnTorch(c + new Vector2( 5f,  4f), 950f);
            SpawnTorch(c + new Vector2( 0f, -7f), 850f);
            SpawnTorch(c + new Vector2( 9f,  1f), 1100f);

            SpawnNpc(
                barbarianNpcPrefab,
                "Hearth-Smith Torin",
                "Master Smith of Oakhaven",
                "q_alpine_summit",
                "Welcome to Oakhaven. The northern ridge still holds blue quartz from the First Hearth—bring back the summit relic and I'll forge your blade.",
                c + new Vector2(6.0f, 1.5f),
                -110f
            );
        }

        private void BuildRiverBridgeOutpost(Vector2 c)
        {
            // Bridge spanning across the carved river right above y = 0.0 waterline
            SpawnProp(bridgePrefab, "SunkenCrossing_Bridge", c, 90f, 7.5f, 0f, true, useFixedY: true, fixedY: 0.9f);
            SpawnProp(cratesPrefab, "Crossing_Crates", c + new Vector2(14f,  2f), 40f, 1.25f, 0.05f, true);
            SpawnProp(barrelPrefab, "Crossing_Barrel", c + new Vector2(13f, -3f), 110f, 1.25f, 0.05f, true);
            SpawnTorch(c + new Vector2(13.5f, 0f), 1000f);

            SpawnNpc(
                rogueNpcPrefab,
                "Aldric the Ferryman",
                "Watcher of the Sunken Crossing",
                "q_river_crossing",
                "Watch your footing near the bank. The glacial melt swollen this river three winters back—only this bridge still connects the western woods.",
                c + new Vector2(15.0f, -1.0f),
                -90f
            );
        }

        private void BuildAncientRuneSanctuary(Vector2 c)
        {
            for (int i = 0; i < 6; i++)
            {
                float ang = (i / 6f) * Mathf.PI * 2f;
                Vector2 off = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 8.0f;
                SpawnProp(pillarPrefab, $"RuneSanctuary_Pillar_{i}", c + off, ang * Mathf.Rad2Deg, 1.7f, 0.25f, true);
            }
            SpawnProp(archPrefab,   "RuneSanctuary_ArchN",  c + new Vector2(0f,  10f), 0f,   1.85f, 0.30f, true);
            SpawnProp(archPrefab,   "RuneSanctuary_ArchS",  c + new Vector2(0f, -10f), 180f, 1.85f, 0.30f, true);
            SpawnProp(rubblePrefab, "RuneSanctuary_Rubble", c + new Vector2(-5f, -4f),  35f,  1.50f, 0.15f, true);
            SpawnProp(chestPrefab,  "RuneSanctuary_Chest",  c + new Vector2(0f,  2.5f), 180f, 1.35f, 0.05f, true);

            SpawnTorch(c + new Vector2(-3.5f, 4f), 1150f);
            SpawnTorch(c + new Vector2( 3.5f, 4f), 1150f);

            SpawnNpc(
                mageNpcPrefab,
                "Runekeeper Astrid",
                "Seer of the High Stones",
                "q_rune_sanctuary",
                "From this ridge you can see the whole valley—the river ford, Oakhaven's smoke, and the snow crown to the north.",
                c + new Vector2(-2.2f, 0.5f),
                125f
            );
        }

        private void BuildNorthernSummitShrine(Vector2 c)
        {
            SpawnProp(archPrefab,   "Summit_Arch",   c + new Vector2(0f,  4f), 0f,   1.8f, 0.45f, true);
            SpawnProp(pillarPrefab, "Summit_PillarL", c + new Vector2(-4f, 0f), 20f, 1.6f, 0.35f, true);
            SpawnProp(pillarPrefab, "Summit_PillarR", c + new Vector2( 4f, 0f), -20f, 1.6f, 0.35f, true);
            SpawnProp(chestPrefab,  "Summit_Relic",  c, 180f, 1.45f, 0.05f, true);
            SpawnTorch(c + new Vector2(-2.5f, 2f), 1200f);
            SpawnTorch(c + new Vector2( 2.5f, 2f), 1200f);
        }

        private void SpawnProp(
            GameObject prefab,
            string label,
            Vector2 xz,
            float yawDeg,
            float scale,
            float sinkDepth,
            bool addCollider,
            bool useFixedY = false,
            float fixedY = 0f)
        {
            if (prefab == null) return;
            float y = useFixedY ? fixedY : SampleGroundY(xz.x, xz.y) - sinkDepth;
            var go = Instantiate(prefab, new Vector3(xz.x, y, xz.y), Quaternion.Euler(0f, yawDeg, 0f), transform);
            go.name = label;
            go.transform.localScale = Vector3.one * scale;

            if (addCollider)
            {
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh != null && mf.GetComponent<Collider>() == null)
                    {
                        var mc = mf.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                    }
                }
            }
        }

        private void SpawnTorch(Vector2 xz, float lumens)
        {
            float y = SampleGroundY(xz.x, xz.y) - 0.08f;
            var root = new GameObject("Oakhaven_Torch");
            root.transform.SetParent(transform, false);
            root.transform.position = new Vector3(xz.x, y, xz.y);

            if (torchPrefab != null)
            {
                var visual = Instantiate(torchPrefab, root.transform);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localScale = Vector3.one * 1.45f;
            }

            var lightGo = new GameObject("TorchLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1.0f, 0.62f, 0.24f);
            l.range = 18f;
            var hd = lightGo.AddComponent<HDAdditionalLightData>();
            hd.SetIntensity(lumens, LightUnit.Lumen);
            hd.EnableShadows(false);
            _flickerLights.Add((l, hd, lumens, xz.x * 0.37f + xz.y * 0.19f));
        }

        private void SpawnNpc(GameObject prefab, string npcName, string title, string questId, string greeting, Vector2 xz, float yawDeg)
        {
            if (prefab == null) return;
            float y = SampleGroundY(xz.x, xz.y);
            var go = Instantiate(prefab, new Vector3(xz.x, y, xz.y), Quaternion.Euler(0f, yawDeg, 0f), transform);
            go.name = $"NPC_{npcName.Replace(" ", "_")}";

            var npc = go.AddComponent<WorldMillNpc>();
            npc.npcName = npcName;
            npc.npcTitle = title;
            npc.questId = questId;
            npc.greeting = greeting;
            npc.playerTransform = playerTransform;
            npc.rpgStore = rpgStore;
        }
    }
}
