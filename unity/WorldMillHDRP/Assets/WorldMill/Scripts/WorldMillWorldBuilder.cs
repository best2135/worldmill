using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using WorldMill.UI;

namespace WorldMill
{
    /// <summary>
    /// Places hand-composed RPG worldbuilding landmarks (Oakhaven Crossing Hamlet,
    /// The Sunken River Bridge, and the Ancient Hilltop Rune Sanctuary) plus rigged
    /// Quest-Giver NPCs, lit HDRP torches, and hearth fires. Automatically grounds each
    /// structure onto streamed chunk MeshColliders as soon as terrain loads underneath.
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
        public WorldMillStreamer streamer;
        public WorldMillRpgClient rpgClient;
        public Transform playerTransform;

        private sealed class PendingPoi
        {
            public GameObject Instance;
            public Vector2 TargetXZ;
            public float SinkDepth;
            public float FixedY;
            public bool UseFixedY;
            public bool Grounded;
        }

        private readonly List<PendingPoi> _pois = new();
        private readonly List<(Light light, HDAdditionalLightData hd, float baseLum, float phase)> _flickerLights = new();

        private void Start()
        {
            BuildOakhavenHamlet(new Vector2(12f, -34f));
            BuildRiverBridgeOutpost(new Vector2(-4f, -30f));
            BuildAncientRuneSanctuary(new Vector2(40f, -10f));
        }

        private void Update()
        {
            // Ground any ungrounded POIs as soon as their streamed terrain MeshCollider exists
            for (int i = 0; i < _pois.Count; i++)
            {
                var p = _pois[i];
                if (p.Grounded || p.Instance == null) continue;

                if (p.UseFixedY)
                {
                    p.Instance.transform.position = new Vector3(p.TargetXZ.x, p.FixedY, p.TargetXZ.y);
                    p.Instance.SetActive(true);
                    p.Grounded = true;
                    continue;
                }

                Vector3 probe = new Vector3(p.TargetXZ.x, 120f, p.TargetXZ.y);
                if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 220f))
                {
                    // Skip placing dry buildings underwater; clamp slightly above shoreline if needed
                    float y = Mathf.Max(hit.point.y - p.SinkDepth, -5.6f);
                    p.Instance.transform.position = new Vector3(p.TargetXZ.x, y, p.TargetXZ.y);
                    p.Instance.SetActive(true);
                    p.Grounded = true;
                }
            }

            // Warm organic flicker on village torches & braziers
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

        private void BuildOakhavenHamlet(Vector2 c)
        {
            // Medieval buildings scaled ~4.8x from KayKit hex scale to grand human RPG scale
            SpawnProp(tavernPrefab,     "Oakhaven_Tavern",     c + new Vector2(-10f,  6f), 145f, 5.2f, 0.65f, true);
            SpawnProp(blacksmithPrefab, "Oakhaven_Smithy",     c + new Vector2( 11f,  5f), 220f, 5.0f, 0.65f, true);
            SpawnProp(homeAPrefab,      "Oakhaven_CottageA",   c + new Vector2(-12f, -9f),  45f, 4.8f, 0.65f, true);
            SpawnProp(homeBPrefab,      "Oakhaven_CottageB",   c + new Vector2( 13f, -10f), -55f, 4.8f, 0.65f, true);
            SpawnProp(marketPrefab,     "Oakhaven_Market",     c + new Vector2(  2f, 11f), 185f, 4.8f, 0.55f, true);
            SpawnProp(wellPrefab,       "Oakhaven_Well",       c + new Vector2(  0f,  0f),  20f, 4.2f, 0.35f, true);
            SpawnProp(towerPrefab,      "Oakhaven_Watchtower", c + new Vector2( 22f, 14f), 210f, 5.8f, 0.90f, true);
            SpawnProp(windmillPrefab,   "Oakhaven_Windmill",   c + new Vector2(-24f, 18f), 120f, 5.6f, 0.85f, true);

            // Clutter props (barrels, crates) for lived-in Witcher/Valheim detail
            SpawnProp(barrelPrefab, "Oakhaven_Barrels1", c + new Vector2(-5f,  3f), 30f, 1.3f, 0.08f, true);
            SpawnProp(cratesPrefab, "Oakhaven_Crates1",  c + new Vector2( 6f,  2f), 75f, 1.3f, 0.08f, true);
            SpawnProp(cratesPrefab, "Oakhaven_Crates2",  c + new Vector2(-6f, -5f), 15f, 1.2f, 0.08f, true);

            // Lit HDRP torches illuminating the village square & forge
            SpawnTorch(c + new Vector2(-4f,  4f), 950f);
            SpawnTorch(c + new Vector2( 5f,  4f), 950f);
            SpawnTorch(c + new Vector2( 0f, -6f), 850f);
            SpawnTorch(c + new Vector2( 9f,  1f), 1100f);

            // Rigged Quest-Giver NPC: Hearth-Smith Torin
            SpawnNpc(
                barbarianNpcPrefab,
                "Hearth-Smith Torin",
                "Master Smith of Oakhaven",
                "q_crystal_heart",
                "Welcome to Oakhaven, traveler. Beneath these mountains pulse veins of Heartcrystal—bring back proof from the deep caverns and I'll temper your steel.",
                c + new Vector2(5.5f, 1.5f),
                -110f
            );
        }

        private void BuildRiverBridgeOutpost(Vector2 c)
        {
            // Bridge perched across the carved river waterline (y ~ -5.2m)
            SpawnProp(bridgePrefab, "SunkenCrossing_Bridge", c, 90f, 6.0f, 0f, true, useFixedY: true, fixedY: -5.2f);
            SpawnProp(cratesPrefab, "Crossing_Crates", c + new Vector2(7f, 2f), 40f, 1.25f, 0.08f, true);
            SpawnProp(barrelPrefab, "Crossing_Barrel", c + new Vector2(6f, -3f), 110f, 1.25f, 0.08f, true);
            SpawnTorch(c + new Vector2(6.5f, 0f), 1000f);

            // Rigged Quest-Giver NPC: Aldric the Ferryman
            SpawnNpc(
                rogueNpcPrefab,
                "Aldric the Ferryman",
                "Watcher of the Sunken Crossing",
                "q_river_crossing",
                "The river rose three winters back and drowned the old king's road. Cross the waters west—or follow the cliffs up toward Astrid's sanctuary.",
                c + new Vector2(8.0f, -1.2f),
                -90f
            );
        }

        private void BuildAncientRuneSanctuary(Vector2 c)
        {
            // Circle of 6 ancient carved stone pillars + ruined arches + treasure chest!
            for (int i = 0; i < 6; i++)
            {
                float ang = (i / 6f) * Mathf.PI * 2f;
                Vector2 off = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 7.5f;
                SpawnProp(pillarPrefab, $"RuneSanctuary_Pillar_{i}", c + off, ang * Mathf.Rad2Deg, 1.65f, 0.35f, true);
            }
            SpawnProp(archPrefab,   "RuneSanctuary_ArchN",  c + new Vector2(0f,  9.5f), 0f,   1.85f, 0.45f, true);
            SpawnProp(archPrefab,   "RuneSanctuary_ArchS",  c + new Vector2(0f, -9.5f), 180f, 1.85f, 0.45f, true);
            SpawnProp(rubblePrefab, "RuneSanctuary_Rubble", c + new Vector2(-5f, -4f),  35f,  1.50f, 0.20f, true);
            SpawnProp(chestPrefab,  "RuneSanctuary_Chest",  c + new Vector2(0f,  2.5f), 180f, 1.35f, 0.05f, true);

            SpawnTorch(c + new Vector2(-3.5f, 4f), 1150f);
            SpawnTorch(c + new Vector2( 3.5f, 4f), 1150f);

            // Rigged Quest-Giver NPC: Runekeeper Astrid
            SpawnNpc(
                mageNpcPrefab,
                "Runekeeper Astrid",
                "Seer of the Sky Arches",
                "q_sky_arches",
                "Look skyward—the monolithic arches still hold the old world's weight. Reach their crest and the runes will answer.",
                c + new Vector2(-2.2f, 0.5f),
                125f
            );
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
            var go = Instantiate(prefab, transform);
            go.name = label;
            go.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            go.transform.localScale = Vector3.one * scale;
            go.SetActive(false);

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

            _pois.Add(new PendingPoi
            {
                Instance = go,
                TargetXZ = xz,
                SinkDepth = sinkDepth,
                FixedY = fixedY,
                UseFixedY = useFixedY,
            });
        }

        private void SpawnTorch(Vector2 xz, float lumens)
        {
            var root = new GameObject("Oakhaven_Torch");
            root.transform.SetParent(transform, false);
            root.SetActive(false);

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
            hd.EnableShadows(false); // Keep village lighting ultra-fast at 120+ FPS
            _flickerLights.Add((l, hd, lumens, xz.x * 0.37f + xz.y * 0.19f));

            _pois.Add(new PendingPoi
            {
                Instance = root,
                TargetXZ = xz,
                SinkDepth = 0.08f,
            });
        }

        private void SpawnNpc(GameObject prefab, string npcName, string title, string questId, string greeting, Vector2 xz, float yawDeg)
        {
            if (prefab == null) return;
            var go = Instantiate(prefab, transform);
            go.name = $"NPC_{npcName.Replace(" ", "_")}";
            go.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            go.SetActive(false);

            var npc = go.AddComponent<WorldMillNpc>();
            npc.npcName = npcName;
            npc.npcTitle = title;
            npc.questId = questId;
            npc.greeting = greeting;
            npc.playerTransform = playerTransform;
            npc.rpgClient = rpgClient;

            _pois.Add(new PendingPoi
            {
                Instance = go,
                TargetXZ = xz,
                SinkDepth = 0.02f,
            });
        }
    }
}
