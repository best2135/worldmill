using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill.UI
{
    [Serializable]
    public sealed class RpgInventoryItem
    {
        public string id;
        public string name;
        public string kind;
        public int quantity = 1;
        public string description;

        public int count => quantity;
    }

    [Serializable]
    public sealed class RpgLoreEntry
    {
        public string title;
        public string text;
    }

    [Serializable]
    public sealed class RpgPlayerState
    {
        public int level = 1;
        public int xp = 0;
        public int xp_to_next = 100;
        public float hp = 100f;
        public float max_hp = 100f;
        public float stamina = 100f;
        public float max_stamina = 100f;
        public string equipped_weapon = "Weathered Iron Axe";
        public string equipped_armor = "Wanderer's Wool Cloak";
        public string active_quest_id;
        public string[] completed_quests = Array.Empty<string>();
        public RpgInventoryItem[] inventory = Array.Empty<RpgInventoryItem>();
    }

    [Serializable]
    public sealed class RpgQuest
    {
        public string id;
        public string title;
        public string giver;
        public string description;
        public float[] target = new float[3];
        public string[] objectives = Array.Empty<string>();
        public RpgLoreEntry[] lore = Array.Empty<RpgLoreEntry>();
        public int reward_xp;
        public RpgInventoryItem reward_item;
        public string status;

        public Vector3 TargetPosition =>
            target != null && target.Length >= 3
                ? new Vector3(target[0], target[1], target[2])
                : Vector3.zero;

        public string ShortObjective =>
            objectives != null && objectives.Length > 0 && !string.IsNullOrEmpty(objectives[0])
                ? objectives[0]
                : description;

        public int XpReward => reward_xp;
        public string ItemRewardName => reward_item != null ? reward_item.name : string.Empty;
    }

    [Serializable]
    public sealed class RpgStructure
    {
        public ulong id;
        public string kind;
        public float[] position = new float[3];
        public float rotation_y;
        public uint seed;

        [NonSerialized] public string LocalKey;

        public Vector3 UnityPosition =>
            position != null && position.Length >= 3
                ? new Vector3(position[0], position[1], position[2])
                : Vector3.zero;
    }

    [Serializable]
    public sealed class RpgStateResponse
    {
        public RpgPlayerState player;
        public RpgQuest[] quests = Array.Empty<RpgQuest>();
        public RpgStructure[] structures = Array.Empty<RpgStructure>();
    }

    [Serializable]
    internal sealed class RpgStructureEnvelope
    {
        public RpgStructure structure = null;
    }

    /// <summary>
    /// HTTP client + persistent world-object manager for WorldMill's Rust RPG backend.
    /// Uses System.Net.Http.HttpClient on background tasks with ConcurrentQueue dispatch
    /// onto the Unity main thread and graceful offline fallback quests.
    /// </summary>
    public sealed class WorldMillRpgClient : MonoBehaviour
    {
        private static readonly HttpClient SharedHttp = new()
        {
            Timeout = TimeSpan.FromSeconds(4.0)
        };

        [Header("Backend Endpoint")]
        public string baseUrl = "http://127.0.0.1:8080";
        public bool autoFetchOnStart = true;

        [Header("Quest Interaction")]
        public float questCompletionRadius = 18f;
        public KeyCode completeQuestKey = KeyCode.E;
        public Transform playerTransform;

        [Header("References")]
        public WorldMillStreamer streamer;

        public RpgStateResponse CurrentState { get; private set; }
        public RpgQuest ActiveQuest { get; private set; }
        public IReadOnlyList<RpgStructure> PlacedStructures => _placedStructures;

        public bool IsOnline { get; private set; }
        public bool IsOfflineFallback { get; private set; }
        public bool IsRequestInFlight { get; private set; }
        public string LastSyncStatus { get; private set; } = "Initializing...";
        public string LastToastMessage { get; private set; }
        public float LastToastTime { get; private set; } = -999f;

        public bool IsNearActiveQuestTarget { get; private set; }
        public bool CanCompleteActiveQuest { get; private set; }
        public float DistanceToActiveQuest { get; private set; } = float.PositiveInfinity;

        public event Action<RpgStateResponse> OnStateUpdated;
        public event Action<IReadOnlyList<RpgStructure>> OnStructuresSynced;
        public event Action<RpgStructure> OnStructurePlaced;
        public event Action<RpgQuest> OnQuestCompleted;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private readonly List<RpgStructure> _placedStructures = new();
        private readonly Dictionary<string, GameObject> _spawnedStructureObjects = new();
        private readonly HashSet<string> _completedQuestSet = new();

        private CancellationTokenSource _cts;
        private Transform _structureRoot;
        private bool _completingQuest;
        private int _localStructureCounter;

        private static Mesh _cachedStoneMesh;
        private static Mesh _cachedLogMesh;
        private static Mesh _cachedEmberMesh;
        private static Material _cachedStoneMat;
        private static Material _cachedCharredWoodMat;
        private static Material _cachedEmberMat;

        private void Awake()
        {
            if (streamer == null)
                streamer = FindFirstObjectByType<WorldMillStreamer>();

            var rootGo = new GameObject("WorldMill_RPG_Structures");
            rootGo.transform.SetParent(transform, false);
            _structureRoot = rootGo.transform;

            ApplyState(CreateDefaultOfflineState(), isOnline: false, isFallback: true, status: "Offline Sanctuary Defaults");
        }

        private void OnEnable()
        {
            _cts = new CancellationTokenSource();
        }

        private void Start()
        {
            if (autoFetchOnStart)
                RefreshState();
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WorldMill RPG] Main-thread callback error: {ex.Message}");
                }
            }

            UpdateQuestProximity();
        }

        /// <summary>
        /// Fetch full player, quest, and persistent structure state from GET /api/rpg/state.
        /// </summary>
        public void RefreshState()
        {
            if (_cts == null) return;
            var ct = _cts.Token;
            string url = BuildUrl("/api/rpg/state");
            IsRequestInFlight = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var resp = await SharedHttp.GetAsync(url, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                    var parsed = JsonUtility.FromJson<RpgStateResponse>(body);
                    if (parsed == null || parsed.player == null)
                        throw new InvalidOperationException("Empty or invalid /api/rpg/state JSON");

                    _mainThreadQueue.Enqueue(() =>
                    {
                        IsRequestInFlight = false;
                        ApplyState(parsed, isOnline: true, isFallback: false, status: "Synced with Rust Backend");
                    });
                }
                catch (OperationCanceledException)
                {
                    _mainThreadQueue.Enqueue(() => IsRequestInFlight = false);
                }
                catch (Exception ex)
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        IsRequestInFlight = false;
                        IsOnline = false;
                        IsOfflineFallback = true;
                        LastSyncStatus = $"Offline Fallback ({ShortError(ex.Message)})";
                    });
                }
            }, ct);
        }

        /// <summary>
        /// Marks the specified quest as currently tracked on the compass and quest tracker.
        /// </summary>
        public void SetActiveQuest(string questId)
        {
            if (CurrentState == null || CurrentState.player == null || string.IsNullOrEmpty(questId))
                return;

            RpgQuest found = FindQuest(questId);
            if (found == null) return;

            CurrentState.player.active_quest_id = questId;
            ActiveQuest = found;
            ShowToast($"Tracking Contract: {found.title}");
            OnStateUpdated?.Invoke(CurrentState);
        }

        public bool IsQuestCompleted(string questId)
        {
            return !string.IsNullOrEmpty(questId) && _completedQuestSet.Contains(questId);
        }

        public void CompleteActiveQuest()
        {
            if (ActiveQuest != null)
                CompleteQuest(ActiveQuest.id);
        }

        /// <summary>
        /// Completes a quest via POST /api/rpg/quests/{id}/complete (or locally if offline).
        /// </summary>
        public void CompleteQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId) || _completingQuest || IsQuestCompleted(questId))
                return;

            var quest = FindQuest(questId);
            if (quest == null) return;

            _completingQuest = true;
            if (_cts == null)
            {
                CompleteQuestOffline(quest);
                _completingQuest = false;
                return;
            }

            var ct = _cts.Token;
            string url = BuildUrl($"/api/rpg/quests/{Uri.EscapeDataString(questId)}/complete");

            _ = Task.Run(async () =>
            {
                try
                {
                    using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var resp = await SharedHttp.PostAsync(url, content, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                    var parsed = JsonUtility.FromJson<RpgStateResponse>(body);
                    if (parsed == null || parsed.player == null)
                        throw new InvalidOperationException("Invalid quest completion response JSON");

                    _mainThreadQueue.Enqueue(() =>
                    {
                        _completingQuest = false;
                        ApplyState(parsed, isOnline: true, isFallback: false, status: "Quest Completed (Synced)");
                        ShowToast($"Contract Complete: {quest.title} (+{quest.reward_xp} XP)");
                        OnQuestCompleted?.Invoke(quest);
                    });
                }
                catch (OperationCanceledException)
                {
                    _mainThreadQueue.Enqueue(() => _completingQuest = false);
                }
                catch (Exception ex)
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        _completingQuest = false;
                        IsOnline = false;
                        IsOfflineFallback = true;
                        LastSyncStatus = $"Offline Completion ({ShortError(ex.Message)})";
                        CompleteQuestOffline(quest);
                    });
                }
            }, ct);
        }

        /// <summary>
        /// Spawns a real HDRP flickering campfire GameObject at unityPos and persists it to POST /api/rpg/structures.
        /// </summary>
        public GameObject PlaceCampfireAt(Vector3 unityPos, float yawDeg, uint seed)
        {
            string localKey = $"local_campfire_{++_localStructureCounter}_{seed}";
            var structure = new RpgStructure
            {
                id = 0,
                LocalKey = localKey,
                kind = "campfire",
                position = new[] { unityPos.x, unityPos.y, unityPos.z },
                rotation_y = yawDeg,
                seed = seed
            };

            GameObject go = SpawnCampfireGameObject(structure);
            _placedStructures.Add(structure);
            OnStructurePlaced?.Invoke(structure);
            ShowToast("Campfire Kindled — Warmth & Rest");

            if (_cts == null) return go;

            var ct = _cts.Token;
            string url = BuildUrl("/api/rpg/structures");
            string payload = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"kind\":\"campfire\",\"position\":[{0:F3},{1:F3},{2:F3}],\"rotation_y\":{3:F2},\"seed\":{4}}}",
                unityPos.x, unityPos.y, unityPos.z, yawDeg, seed
            );

            _ = Task.Run(async () =>
            {
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await SharedHttp.PostAsync(url, content, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

                    var env = JsonUtility.FromJson<RpgStructureEnvelope>(body);
                    RpgStructure persisted = env != null && env.structure != null
                        ? env.structure
                        : JsonUtility.FromJson<RpgStructure>(body);

                    _mainThreadQueue.Enqueue(() =>
                    {
                        IsOnline = true;
                        IsOfflineFallback = false;
                        LastSyncStatus = "Structure Persisted";
                        if (persisted != null && persisted.id != 0)
                            RebindStructureId(localKey, persisted);
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _mainThreadQueue.Enqueue(() =>
                    {
                        IsOnline = false;
                        IsOfflineFallback = true;
                        LastSyncStatus = $"Offline Campfire ({ShortError(ex.Message)})";
                    });
                }
            }, ct);

            return go;
        }

        public void ShowToast(string message)
        {
            LastToastMessage = message;
            LastToastTime = Time.unscaledTime;
        }

        private void UpdateQuestProximity()
        {
            Transform viewer = playerTransform != null
                ? playerTransform
                : (Camera.main != null ? Camera.main.transform : null);

            if (viewer == null || ActiveQuest == null)
            {
                DistanceToActiveQuest = float.PositiveInfinity;
                IsNearActiveQuestTarget = false;
                CanCompleteActiveQuest = false;
                return;
            }

            DistanceToActiveQuest = Vector3.Distance(viewer.position, ActiveQuest.TargetPosition);
            bool alreadyDone = IsQuestCompleted(ActiveQuest.id);
            IsNearActiveQuestTarget = DistanceToActiveQuest <= questCompletionRadius;
            CanCompleteActiveQuest = !alreadyDone && IsNearActiveQuestTarget && !_completingQuest;

            if (CanCompleteActiveQuest && Input.GetKeyDown(completeQuestKey))
            {
                CompleteActiveQuest();
            }
        }

        private void ApplyState(RpgStateResponse state, bool isOnline, bool isFallback, string status)
        {
            CurrentState = state;
            IsOnline = isOnline;
            IsOfflineFallback = isFallback;
            LastSyncStatus = status;

            _completedQuestSet.Clear();
            if (state.player != null && state.player.completed_quests != null)
            {
                foreach (var qid in state.player.completed_quests)
                {
                    if (!string.IsNullOrEmpty(qid))
                        _completedQuestSet.Add(qid);
                }
            }
            if (state.quests != null)
            {
                foreach (var q in state.quests)
                {
                    if (q != null && string.Equals(q.status, "completed", StringComparison.OrdinalIgnoreCase))
                        _completedQuestSet.Add(q.id);
                }
            }

            ActiveQuest = null;
            if (state.quests != null && state.quests.Length > 0)
            {
                if (state.player != null && !string.IsNullOrEmpty(state.player.active_quest_id))
                    ActiveQuest = FindQuest(state.player.active_quest_id);

                if (ActiveQuest == null)
                {
                    foreach (var q in state.quests)
                    {
                        if (!IsQuestCompleted(q.id))
                        {
                            ActiveQuest = q;
                            break;
                        }
                    }
                    ActiveQuest ??= state.quests[0];
                }
            }

            SyncStructuresFromServer(state.structures);
            OnStateUpdated?.Invoke(CurrentState);
        }

        private void SyncStructuresFromServer(RpgStructure[] serverStructures)
        {
            if (serverStructures == null) return;

            bool changed = false;
            foreach (var st in serverStructures)
            {
                if (st == null) continue;
                string key = StructureKey(st);
                if (_spawnedStructureObjects.ContainsKey(key)) continue;

                if (HasNearbySpawnedStructure(st.UnityPosition, 0.35f))
                    continue;

                if (string.Equals(st.kind, "campfire", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(st.kind))
                {
                    SpawnCampfireGameObject(st);
                }

                _placedStructures.Add(st);
                changed = true;
            }

            if (changed)
                OnStructuresSynced?.Invoke(_placedStructures);
        }

        private bool HasNearbySpawnedStructure(Vector3 pos, float epsilon)
        {
            float sqrEps = epsilon * epsilon;
            foreach (var existing in _placedStructures)
            {
                if ((existing.UnityPosition - pos).sqrMagnitude <= sqrEps)
                    return true;
            }
            return false;
        }

        private void RebindStructureId(string oldKey, RpgStructure persisted)
        {
            string newKey = StructureKey(persisted);
            if (_spawnedStructureObjects.TryGetValue(oldKey, out var go))
            {
                _spawnedStructureObjects.Remove(oldKey);
                _spawnedStructureObjects[newKey] = go;
                if (go != null)
                    go.name = $"Campfire_{newKey}";
            }

            for (int i = 0; i < _placedStructures.Count; i++)
            {
                if (StructureKey(_placedStructures[i]) == oldKey)
                {
                    _placedStructures[i] = persisted;
                    break;
                }
            }
        }

        private void CompleteQuestOffline(RpgQuest quest)
        {
            if (CurrentState == null || CurrentState.player == null) return;
            if (!_completedQuestSet.Add(quest.id)) return;

            quest.status = "completed";
            var p = CurrentState.player;
            var completed = new List<string>(p.completed_quests ?? Array.Empty<string>()) { quest.id };
            p.completed_quests = completed.ToArray();

            p.xp += quest.reward_xp;
            while (p.xp >= p.xp_to_next && p.xp_to_next > 0)
            {
                p.xp -= p.xp_to_next;
                p.level += 1;
                p.xp_to_next = 100 + Mathf.Max(0, p.level - 1) * 50;
                p.max_hp = 100f + 15f * Mathf.Max(0, p.level - 1);
                p.hp = p.max_hp;
                p.max_stamina = 100f + 10f * Mathf.Max(0, p.level - 1);
                p.stamina = p.max_stamina;
            }

            if (quest.reward_item != null && !string.IsNullOrEmpty(quest.reward_item.name))
            {
                var inv = new List<RpgInventoryItem>(p.inventory ?? Array.Empty<RpgInventoryItem>());
                var existing = inv.Find(i => string.Equals(i.id, quest.reward_item.id, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(i.name, quest.reward_item.name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.quantity += Mathf.Max(1, quest.reward_item.quantity);
                }
                else
                {
                    inv.Add(new RpgInventoryItem
                    {
                        id = string.IsNullOrEmpty(quest.reward_item.id)
                            ? quest.reward_item.name.ToLowerInvariant().Replace(' ', '_')
                            : quest.reward_item.id,
                        name = quest.reward_item.name,
                        kind = string.IsNullOrEmpty(quest.reward_item.kind) ? "trinket" : quest.reward_item.kind,
                        quantity = Mathf.Max(1, quest.reward_item.quantity),
                        description = quest.reward_item.description
                    });
                }
                p.inventory = inv.ToArray();
            }

            if (CurrentState.quests != null)
            {
                p.active_quest_id = string.Empty;
                foreach (var next in CurrentState.quests)
                {
                    if (!IsQuestCompleted(next.id))
                    {
                        p.active_quest_id = next.id;
                        next.status = "active";
                        ActiveQuest = next;
                        break;
                    }
                }
            }

            ShowToast($"Contract Complete: {quest.title} (+{quest.reward_xp} XP)");
            OnQuestCompleted?.Invoke(quest);
            OnStateUpdated?.Invoke(CurrentState);
        }

        private RpgQuest FindQuest(string questId)
        {
            if (CurrentState == null || CurrentState.quests == null) return null;
            foreach (var q in CurrentState.quests)
            {
                if (q != null && q.id == questId)
                    return q;
            }
            return null;
        }

        private string BuildUrl(string path)
        {
            string trimmed = string.IsNullOrEmpty(baseUrl) ? "http://127.0.0.1:8080" : baseUrl.TrimEnd('/');
            return trimmed + path;
        }

        private static string StructureKey(RpgStructure st)
        {
            if (st.id != 0) return $"id_{st.id}";
            if (!string.IsNullOrEmpty(st.LocalKey)) return st.LocalKey;
            var p = st.UnityPosition;
            return string.Format(CultureInfo.InvariantCulture, "{0}_{1:F2}_{2:F2}_{3:F2}", st.kind, p.x, p.y, p.z);
        }

        private static string ShortError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "Unreachable";
            int nl = msg.IndexOfAny(new[] { '\r', '\n' });
            string clean = nl >= 0 ? msg.Substring(0, nl) : msg;
            return clean.Length > 38 ? clean.Substring(0, 38) + "..." : clean;
        }

        private GameObject SpawnCampfireGameObject(RpgStructure st)
        {
            EnsureCampfireAssets();

            string key = StructureKey(st);
            var root = new GameObject($"Campfire_{key}");
            root.transform.SetParent(_structureRoot, false);
            root.transform.position = st.UnityPosition;
            root.transform.rotation = Quaternion.Euler(0f, st.rotation_y, 0f);

            var rng = new System.Random((int)(st.seed == 0 ? 42u : st.seed));

            // 1. Ring of procedural stone meshes
            const int stoneCount = 9;
            for (int i = 0; i < stoneCount; i++)
            {
                float angle = (i / (float)stoneCount) * Mathf.PI * 2f + (float)(rng.NextDouble() - 0.5) * 0.18f;
                float radius = 0.56f + (float)(rng.NextDouble() - 0.5) * 0.08f;
                var stone = new GameObject($"Stone_{i}");
                stone.transform.SetParent(root.transform, false);
                stone.transform.localPosition = new Vector3(Mathf.Cos(angle) * radius, 0.06f, Mathf.Sin(angle) * radius);
                stone.transform.localRotation = Quaternion.Euler(
                    (float)rng.NextDouble() * 35f,
                    (float)rng.NextDouble() * 360f,
                    (float)rng.NextDouble() * 35f
                );
                float sx = 0.18f + (float)rng.NextDouble() * 0.09f;
                float sy = 0.13f + (float)rng.NextDouble() * 0.07f;
                float sz = 0.16f + (float)rng.NextDouble() * 0.09f;
                stone.transform.localScale = new Vector3(sx, sy, sz);

                stone.AddComponent<MeshFilter>().sharedMesh = _cachedStoneMesh;
                var mr = stone.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _cachedStoneMat;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
            }

            // 2. Crossed charred logs
            const int logCount = 4;
            for (int i = 0; i < logCount; i++)
            {
                float yaw = i * 90f + 22f + (float)(rng.NextDouble() - 0.5) * 16f;
                var log = new GameObject($"CharredLog_{i}");
                log.transform.SetParent(root.transform, false);
                log.transform.localRotation = Quaternion.Euler(58f + (float)(rng.NextDouble() * 8f), yaw, 0f);
                log.transform.localPosition = Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, 0.14f, -0.16f);
                log.transform.localScale = new Vector3(0.11f, 0.42f, 0.11f);

                log.AddComponent<MeshFilter>().sharedMesh = _cachedLogMesh;
                var mr = log.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _cachedCharredWoodMat;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
            }

            // 3. Glowing ember bed & inner flame cores
            var emberBed = new GameObject("EmberBed");
            emberBed.transform.SetParent(root.transform, false);
            emberBed.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            emberBed.transform.localScale = new Vector3(0.46f, 0.12f, 0.46f);
            emberBed.AddComponent<MeshFilter>().sharedMesh = _cachedEmberMesh;
            var emberRenderer = emberBed.AddComponent<MeshRenderer>();
            emberRenderer.sharedMaterial = _cachedEmberMat;
            emberRenderer.shadowCastingMode = ShadowCastingMode.Off;

            var flameCore = new GameObject("FlameCore");
            flameCore.transform.SetParent(root.transform, false);
            flameCore.transform.localPosition = new Vector3(0f, 0.24f, 0f);
            flameCore.transform.localScale = new Vector3(0.18f, 0.34f, 0.18f);
            flameCore.AddComponent<MeshFilter>().sharedMesh = _cachedEmberMesh;
            var flameRenderer = flameCore.AddComponent<MeshRenderer>();
            flameRenderer.sharedMaterial = _cachedEmberMat;
            flameRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // 4. Warm flickering HDRP Point Light with soft shadows & volumetric scatter
            var lightGo = new GameObject("CampfireLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.52f, 0f);

            var ptLight = lightGo.AddComponent<Light>();
            ptLight.type = LightType.Point;
            ptLight.color = new Color(1.0f, 0.55f, 0.20f);
            ptLight.useColorTemperature = true;
            ptLight.colorTemperature = 2150f;
            ptLight.range = 18f;
            ptLight.shadows = LightShadows.Soft;
            ptLight.lightUnit = LightUnit.Lumen;
            ptLight.intensity = LightUnitUtils.ConvertIntensity(ptLight, 1950f, LightUnit.Lumen, LightUnitUtils.GetNativeLightUnit(ptLight.type));

            var hdLight = lightGo.AddComponent<HDAdditionalLightData>();
            hdLight.SetRange(18f);
            hdLight.EnableShadows(true);
            hdLight.SetShadowResolutionLevel(1);
            hdLight.volumetricDimmer = 1.35f;

            var flicker = root.AddComponent<CampfireFlicker>();
            flicker.Initialize(ptLight, flameCore.transform, st.seed);

            _spawnedStructureObjects[key] = root;
            return root;
        }

        private static void EnsureCampfireAssets()
        {
            if (_cachedStoneMesh == null)
                _cachedStoneMesh = CreateFacetedRockMesh();
            if (_cachedLogMesh == null)
                _cachedLogMesh = CreateHexLogMesh();
            if (_cachedEmberMesh == null)
                _cachedEmberMesh = CreateFacetedRockMesh();

            Shader litShader = Shader.Find("HDRP/Lit");
            if (litShader == null)
                litShader = Shader.Find("Standard");

            if (_cachedStoneMat == null)
            {
                _cachedStoneMat = new Material(litShader)
                {
                    name = "WorldMill_CampfireStone",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _cachedStoneMat.SetColor("_BaseColor", new Color(0.24f, 0.23f, 0.22f));
                _cachedStoneMat.SetFloat("_Smoothness", 0.22f);
                _cachedStoneMat.SetFloat("_Metallic", 0.02f);
                TryValidateHdMaterial(_cachedStoneMat);
            }

            if (_cachedCharredWoodMat == null)
            {
                _cachedCharredWoodMat = new Material(litShader)
                {
                    name = "WorldMill_CampfireLog",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _cachedCharredWoodMat.SetColor("_BaseColor", new Color(0.11f, 0.07f, 0.05f));
                _cachedCharredWoodMat.SetFloat("_Smoothness", 0.12f);
                _cachedCharredWoodMat.SetFloat("_Metallic", 0.0f);
                TryValidateHdMaterial(_cachedCharredWoodMat);
            }

            if (_cachedEmberMat == null)
            {
                _cachedEmberMat = new Material(litShader)
                {
                    name = "WorldMill_CampfireEmbers",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _cachedEmberMat.SetColor("_BaseColor", new Color(0.16f, 0.04f, 0.01f));
                _cachedEmberMat.SetFloat("_Smoothness", 0.1f);
                var glow = new Color(1.0f, 0.34f, 0.04f) * 14f;
                _cachedEmberMat.SetColor("_EmissiveColor", glow);
                _cachedEmberMat.SetFloat("_UseEmissiveIntensity", 0f);
                _cachedEmberMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                _cachedEmberMat.EnableKeyword("_EMISSION");
                HDMaterial.SetEmissiveColor(_cachedEmberMat, glow);
                TryValidateHdMaterial(_cachedEmberMat);
            }
        }

        private static void TryValidateHdMaterial(Material mat)
        {
            if (mat != null && mat.shader != null && mat.shader.name.StartsWith("HDRP/", StringComparison.Ordinal))
                HDMaterial.ValidateMaterial(mat);
        }

        private static Mesh CreateFacetedRockMesh()
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            Vector3[] baseVerts =
            {
                new(-1f,  t,  0f), new( 1f,  t,  0f), new(-1f, -t,  0f), new( 1f, -t,  0f),
                new( 0f, -1f,  t), new( 0f,  1f,  t), new( 0f, -1f, -t), new( 0f,  1f, -t),
                new( t,  0f, -1f), new( t,  0f,  1f), new(-t,  0f, -1f), new(-t,  0f,  1f)
            };
            float[] bumps = { 0.92f, 1.08f, 0.85f, 1.05f, 1.12f, 0.96f, 0.88f, 1.07f, 1.02f, 0.91f, 1.09f, 0.94f };
            for (int i = 0; i < baseVerts.Length; i++)
                baseVerts[i] = baseVerts[i].normalized * (0.5f * bumps[i]);

            int[] tris =
            {
                0,11,5,  0,5,1,   0,1,7,   0,7,10,  0,10,11,
                1,5,9,   5,11,4,  11,10,2, 10,7,6,  7,1,8,
                3,9,4,   3,4,2,   3,2,6,   3,6,8,   3,8,9,
                4,9,5,   2,4,11,  6,2,10,  8,6,7,   9,8,1
            };

            var verts = new Vector3[tris.Length];
            var indices = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                verts[i] = baseVerts[tris[i]];
                indices[i] = i;
            }

            var mesh = new Mesh { name = "ProceduralCampfireRock", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = verts;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateHexLogMesh()
        {
            const int sides = 7;
            var verts = new Vector3[sides * 2 + 2];
            var tris = new List<int>(sides * 12);

            for (int i = 0; i < sides; i++)
            {
                float a = (i / (float)sides) * Mathf.PI * 2f;
                float r = 0.5f * (i % 2 == 0 ? 1.0f : 0.92f);
                float x = Mathf.Cos(a) * r;
                float z = Mathf.Sin(a) * r;
                verts[i] = new Vector3(x, -0.5f, z);
                verts[sides + i] = new Vector3(x * 0.88f, 0.5f, z * 0.88f);
            }
            int bottomCenter = sides * 2;
            int topCenter = sides * 2 + 1;
            verts[bottomCenter] = new Vector3(0f, -0.5f, 0f);
            verts[topCenter] = new Vector3(0f, 0.5f, 0f);

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                tris.Add(i); tris.Add(sides + i); tris.Add(sides + next);
                tris.Add(i); tris.Add(sides + next); tris.Add(next);
                tris.Add(bottomCenter); tris.Add(i); tris.Add(next);
                tris.Add(topCenter); tris.Add(sides + next); tris.Add(sides + i);
            }

            var mesh = new Mesh { name = "ProceduralCampfireLog", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static RpgStateResponse CreateDefaultOfflineState()
        {
            return new RpgStateResponse
            {
                player = new RpgPlayerState
                {
                    level = 1,
                    xp = 0,
                    xp_to_next = 100,
                    hp = 100f,
                    max_hp = 100f,
                    stamina = 100f,
                    max_stamina = 100f,
                    equipped_weapon = "Weathered Iron Axe",
                    equipped_armor = "Wanderer's Wool Cloak",
                    active_quest_id = "q_river_crossing",
                    completed_quests = Array.Empty<string>(),
                    inventory = new[]
                    {
                        new RpgInventoryItem { id = "weathered_iron_axe", name = "Weathered Iron Axe", kind = "weapon", quantity = 1, description = "Notched from a hundred winters of felling birch." },
                        new RpgInventoryItem { id = "wanderers_wool_cloak", name = "Wanderer's Wool Cloak", kind = "armor", quantity = 1, description = "Smells of woodsmoke and wet sheep." },
                        new RpgInventoryItem { id = "timber", name = "Timber", kind = "material", quantity = 20, description = "Rough-split pine, good for walls and floors." }
                    }
                },
                quests = new[]
                {
                    new RpgQuest
                    {
                        id = "q_river_crossing",
                        title = "The Sunken Crossing",
                        giver = "Aldric the Ferryman",
                        description = "Wade out to the drowned crossing, find what keeps the river from receding, and bring word back before the next storm rolls down from the peaks.",
                        target = new[] { 28f, 4f, -32f },
                        objectives = new[] { "Follow the flooded riverbed to the Sunken Crossing", "Recover the drowned keystone from beneath the bridge" },
                        lore = new[]
                        {
                            new RpgLoreEntry { title = "The Bridges of the Old Kings", text = "Long before the river villages, a forgotten people raised arched bridges of fitted granite across every ford in the valley, without mortar or iron." },
                            new RpgLoreEntry { title = "The Drowned Valley", text = "Elders recall three great floods in living memory. After each, something old surfaced in the silt." }
                        },
                        reward_xp = 150,
                        reward_item = new RpgInventoryItem { id = "ferrymans_lantern", name = "Ferryman's Lantern", kind = "trinket", quantity = 1 },
                        status = "active"
                    },
                    new RpgQuest
                    {
                        id = "q_sky_arches",
                        title = "Embers of the Sky Arch",
                        giver = "Runekeeper Astrid",
                        description = "Climb the frozen ridge, rekindle the dying runes of the Sky Arch, and learn who has been stealing their fire.",
                        target = new[] { 40f, 52f, -10f },
                        objectives = new[] { "Climb the frozen ridge to the floating monoliths", "Rekindle the three dimming rune-embers of the Sky Arch" },
                        lore = new[]
                        {
                            new RpgLoreEntry { title = "The Floating Monoliths", text = "Runekeepers believe the stones are the broken ribs of a sky-ship that carried the first smiths down from the stars." }
                        },
                        reward_xp = 300,
                        reward_item = new RpgInventoryItem { id = "runic_ember_shard", name = "Runic Ember Shard", kind = "trinket", quantity = 1 },
                        status = "available"
                    },
                    new RpgQuest
                    {
                        id = "q_crystal_heart",
                        title = "Heart of the Crystal Deep",
                        giver = "Hearth-Smith Torin",
                        description = "Descend into the glowing caverns of the Crystal Deep and carry the Heartcrystal's light back to Torin's forge.",
                        target = new[] { 0f, -18f, 0f },
                        objectives = new[] { "Descend into the glowing caverns of the Crystal Deep", "Return the Heartcrystal to Torin's forge" },
                        lore = new[]
                        {
                            new RpgLoreEntry { title = "The Singing Veins", text = "Whole forests of crystal pulse with a slow blue bioluminescence, like the breathing of something vast and asleep." }
                        },
                        reward_xp = 500,
                        reward_item = new RpgInventoryItem { id = "heartcrystal_core", name = "Heartcrystal Core", kind = "trinket", quantity = 1 },
                        status = "available"
                    }
                },
                structures = Array.Empty<RpgStructure>()
            };
        }
    }

    /// <summary>
    /// Animates warm hearth light intensity, color temperature, and flame core pulse.
    /// </summary>
    internal sealed class CampfireFlicker : MonoBehaviour
    {
        private Light _light;
        private Transform _flameCore;
        private float _seedOffset;
        private Vector3 _baseLightLocalPos;
        private Vector3 _baseFlameScale;

        public void Initialize(Light ptLight, Transform flameCore, uint seed)
        {
            _light = ptLight;
            _flameCore = flameCore;
            _seedOffset = (seed % 997u) * 0.37f;
            if (_light != null)
                _baseLightLocalPos = _light.transform.localPosition;
            if (_flameCore != null)
                _baseFlameScale = _flameCore.localScale;
        }

        private void Update()
        {
            float t = Time.time + _seedOffset;
            float n1 = Mathf.PerlinNoise(t * 5.2f, _seedOffset);
            float n2 = Mathf.PerlinNoise(t * 11.7f, _seedOffset + 19.3f);
            float flicker = 0.72f + n1 * 0.38f + (n2 - 0.5f) * 0.18f;

            if (_light != null)
            {
                _light.colorTemperature = Mathf.Lerp(1920f, 2450f, n1);
                _light.intensity = LightUnitUtils.ConvertIntensity(
                    _light,
                    1900f * flicker,
                    LightUnit.Lumen,
                    LightUnitUtils.GetNativeLightUnit(_light.type)
                );
                _light.transform.localPosition = _baseLightLocalPos + new Vector3(
                    (n2 - 0.5f) * 0.05f,
                    (n1 - 0.5f) * 0.04f,
                    (Mathf.PerlinNoise(_seedOffset, t * 4.8f) - 0.5f) * 0.05f
                );
            }

            if (_flameCore != null)
            {
                float xz = 0.88f + n1 * 0.24f;
                float y = 0.82f + n2 * 0.42f;
                _flameCore.localScale = new Vector3(_baseFlameScale.x * xz, _baseFlameScale.y * y, _baseFlameScale.z * xz);
            }
        }
    }
}
