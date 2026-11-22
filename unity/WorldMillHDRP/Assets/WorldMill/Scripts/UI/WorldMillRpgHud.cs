using System;
using System.Text;
using UnityEngine;

namespace WorldMill.UI
{
    /// <summary>
    /// Witcher 3 x Valheim atmospheric RPG HUD rendered via zero-asset cached IMGUI textures:
    /// Top-Center Compass Ribbon, Bottom-Left Player Vitals, Top-Right Quest Tracker,
    /// [J] Quest &amp; Lore Codex Overlay, [H] Cinematic HUD Toggle, and RTX Telemetry Badge.
    /// </summary>
    public sealed class WorldMillRpgHud : MonoBehaviour
    {
        [Header("References")]
        public WorldMillRpgClient rpgClient;
        public WorldMillStreamer streamer;
        public Transform playerTransform;

        [Header("Live Player Telemetry (Driven by PlayerController)")]
        public float CurrentHp = 100f;
        public float MaxHp = 100f;
        public float CurrentStamina = 100f;
        public float MaxStamina = 100f;
        public bool TorchLit = false;
        public bool IsSwimming = false;
        public bool IsFlyMode = true;
        public float SunElevationDeg = 28f;

        [Header("HUD Controls")]
        public KeyCode toggleCodexKey = KeyCode.J;
        public KeyCode toggleHudKey = KeyCode.H;

        public bool IsCodexOpen => _codexOpen;
        public bool IsHudVisible => _hudVisible;

        private bool _hudVisible = true;
        private bool _codexOpen = false;
        private string _selectedQuestId;
        private Vector2 _questListScroll;
        private Vector2 _loreScroll;
        private Vector2 _inventoryScroll;
        private float _fpsSmooth = 60f;
        private float _displayHp = 100f;
        private float _displayStamina = 100f;
        private float _displayXp = 0f;
        private bool _hasSavedCursorState;
        private CursorLockMode _prevCursorLock;
        private bool _prevCursorVisible;

        private Texture2D _texPanelDark;
        private Texture2D _texPanelDeep;
        private Texture2D _texGoldBorder;
        private Texture2D _texIronBorder;
        private Texture2D _texBarTrack;
        private Texture2D _texHpFill;
        private Texture2D _texStaminaFill;
        private Texture2D _texXpFill;
        private Texture2D _texButtonNormal;
        private Texture2D _texButtonHover;
        private Texture2D _texButtonActive;
        private Texture2D _texDimOverlay;

        private GUIStyle _lblTitle;
        private GUIStyle _lblSubtitle;
        private GUIStyle _lblBody;
        private GUIStyle _lblMuted;
        private GUIStyle _lblCenterGold;
        private GUIStyle _lblCenterWhite;
        private GUIStyle _lblCenterSmall;
        private GUIStyle _lblCompassTick;
        private GUIStyle _lblCompassCard;
        private GUIStyle _lblTelemetry;
        private GUIStyle _lblHint;
        private GUIStyle _btnStyle;
        private GUIStyle _btnSelectedStyle;
        private bool _stylesBuilt;

        private static readonly (float deg, string label, bool major)[] CompassTicks = BuildCompassTicks();

        private void Awake()
        {
            if (rpgClient == null)
                rpgClient = FindFirstObjectByType<WorldMillRpgClient>();
            if (streamer == null)
                streamer = FindFirstObjectByType<WorldMillStreamer>();
        }

        private void OnEnable()
        {
            if (rpgClient != null)
                rpgClient.OnStateUpdated += HandleStateUpdated;
        }

        private void OnDisable()
        {
            if (rpgClient != null)
                rpgClient.OnStateUpdated -= HandleStateUpdated;

            RestoreCursorIfSaved();
        }

        private void OnDestroy()
        {
            DestroyTex(ref _texPanelDark);
            DestroyTex(ref _texPanelDeep);
            DestroyTex(ref _texGoldBorder);
            DestroyTex(ref _texIronBorder);
            DestroyTex(ref _texBarTrack);
            DestroyTex(ref _texHpFill);
            DestroyTex(ref _texStaminaFill);
            DestroyTex(ref _texXpFill);
            DestroyTex(ref _texButtonNormal);
            DestroyTex(ref _texButtonHover);
            DestroyTex(ref _texButtonActive);
            DestroyTex(ref _texDimOverlay);
        }

        public void SetVitals(float hp, float maxHp, float stamina, float maxStamina)
        {
            CurrentHp = hp;
            MaxHp = maxHp;
            CurrentStamina = stamina;
            MaxStamina = maxStamina;
        }

        public void SetCodexOpen(bool open)
        {
            if (_codexOpen == open) return;
            _codexOpen = open;
            if (_codexOpen)
            {
                _prevCursorLock = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
                _hasSavedCursorState = true;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                if (string.IsNullOrEmpty(_selectedQuestId) && rpgClient?.ActiveQuest != null)
                    _selectedQuestId = rpgClient.ActiveQuest.id;
            }
            else
            {
                RestoreCursorIfSaved();
            }
        }

        private void RestoreCursorIfSaved()
        {
            if (!_hasSavedCursorState) return;
            Cursor.lockState = _prevCursorLock;
            Cursor.visible = _prevCursorVisible;
            _hasSavedCursorState = false;
        }

        private void HandleStateUpdated(RpgStateResponse state)
        {
            if (state?.player == null) return;
            MaxHp = Mathf.Max(1f, state.player.max_hp);
            MaxStamina = Mathf.Max(1f, state.player.max_stamina);
            if (string.IsNullOrEmpty(_selectedQuestId) && rpgClient?.ActiveQuest != null)
                _selectedQuestId = rpgClient.ActiveQuest.id;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f)
                _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / dt, 0.1f);

            _displayHp = Mathf.Lerp(_displayHp, Mathf.Clamp(CurrentHp, 0f, Mathf.Max(1f, MaxHp)), 1f - Mathf.Exp(-12f * dt));
            _displayStamina = Mathf.Lerp(_displayStamina, Mathf.Clamp(CurrentStamina, 0f, Mathf.Max(1f, MaxStamina)), 1f - Mathf.Exp(-14f * dt));

            float targetXp = rpgClient?.CurrentState?.player != null ? rpgClient.CurrentState.player.xp : 0f;
            _displayXp = Mathf.Lerp(_displayXp, targetXp, 1f - Mathf.Exp(-8f * dt));

            if (Input.GetKeyDown(toggleHudKey) && !_codexOpen)
                _hudVisible = !_hudVisible;

            if (Input.GetKeyDown(toggleCodexKey))
                SetCodexOpen(!_codexOpen);
            else if (_codexOpen && Input.GetKeyDown(KeyCode.Escape))
                SetCodexOpen(false);
        }

        private void OnGUI()
        {
            if (!_hudVisible && !_codexOpen)
                return;

            EnsureTexturesAndStyles();

            float uiScale = Mathf.Clamp(Screen.height / 1080f, 0.82f, 2.0f);
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

            float vw = Screen.width / uiScale;
            float vh = Screen.height / uiScale;

            try
            {
                if (_hudVisible)
                {
                    DrawCompassRibbon(vw);
                    DrawPlayerVitals(vh);
                    DrawQuestTracker(vw);
                    DrawInteractionPrompt(vw, vh);
                    DrawToastNotification(vw);
                    DrawTelemetryBadge(vw, vh);
                }

                if (_codexOpen)
                {
                    DrawCodexModal(vw, vh);
                }
            }
            finally
            {
                GUI.matrix = prevMatrix;
            }
        }

        private void DrawCompassRibbon(float vw)
        {
            const float width = 520f;
            const float height = 42f;
            float x = (vw - width) * 0.5f;
            const float y = 14f;

            var rect = new Rect(x, y, width, height);
            DrawFramedBox(rect, _texPanelDark, _texGoldBorder, 1f);

            Transform cam = Camera.main != null ? Camera.main.transform : playerTransform;
            float heading = cam != null ? Mathf.Repeat(cam.eulerAngles.y, 360f) : 0f;

            GUI.Label(new Rect(x + width * 0.5f - 10f, y - 3f, 20f, 16f), "▼", _lblCenterGold);

            const float halfFovDeg = 95f;
            GUI.BeginGroup(new Rect(x + 10f, y + 4f, width - 20f, height - 8f));
            float innerW = width - 20f;
            float midX = innerW * 0.5f;

            foreach (var tick in CompassTicks)
            {
                float delta = Mathf.DeltaAngle(heading, tick.deg);
                if (Mathf.Abs(delta) > halfFovDeg) continue;

                float tx = midX + (delta / halfFovDeg) * midX;
                float fade = 1f - Mathf.SmoothStep(halfFovDeg * 0.65f, halfFovDeg, Mathf.Abs(delta));
                Color prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, fade);

                if (tick.major)
                {
                    GUI.DrawTexture(new Rect(tx - 0.5f, 1f, 1f, 6f), _texGoldBorder);
                    GUI.Label(new Rect(tx - 18f, 9f, 36f, 22f), tick.label, _lblCompassCard);
                }
                else
                {
                    GUI.DrawTexture(new Rect(tx - 0.5f, 2f, 1f, 4f), _texIronBorder);
                    GUI.Label(new Rect(tx - 16f, 11f, 32f, 20f), tick.label, _lblCompassTick);
                }
                GUI.color = prev;
            }

            // Nearby campfire ember markers
            if (rpgClient != null && cam != null)
            {
                foreach (var st in rpgClient.PlacedStructures)
                {
                    Vector3 flat = st.UnityPosition - cam.position;
                    flat.y = 0f;
                    float dist = flat.magnitude;
                    if (dist < 1f || dist > 350f) continue;

                    float bearing = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                    float delta = Mathf.DeltaAngle(heading, bearing);
                    if (Mathf.Abs(delta) > halfFovDeg) continue;

                    float mx = midX + (delta / halfFovDeg) * midX;
                    Color prev = GUI.color;
                    GUI.color = new Color(1f, 0.55f, 0.2f, 0.9f);
                    GUI.Label(new Rect(mx - 10f, 0f, 20f, 18f), "▲", _lblCompassTick);
                    GUI.color = prev;
                }
            }

            // Active quest golden diamond waypoint marker
            if (rpgClient?.ActiveQuest != null && cam != null)
            {
                Vector3 toTarget = rpgClient.ActiveQuest.TargetPosition - cam.position;
                float dist = toTarget.magnitude;
                Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
                float bearing = flat.sqrMagnitude > 0.01f
                    ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg
                    : heading;

                float delta = Mathf.Clamp(Mathf.DeltaAngle(heading, bearing), -halfFovDeg + 6f, halfFovDeg - 6f);
                float wx = midX + (delta / halfFovDeg) * midX;
                string marker = $"◆ {Mathf.RoundToInt(dist)}m";
                GUI.Label(new Rect(wx - 44f, 14f, 88f, 20f), marker, _lblCenterGold);
            }

            GUI.EndGroup();
        }

        private void DrawPlayerVitals(float vh)
        {
            const float panelW = 348f;
            const float panelH = 148f;
            const float x = 22f;
            float y = vh - panelH - 22f;

            DrawFramedBox(new Rect(x, y, panelW, panelH), _texPanelDark, _texGoldBorder, 1f);

            var player = rpgClient?.CurrentState?.player;
            int level = player != null ? player.level : 1;
            int xpNext = player != null && player.xp_to_next > 0 ? player.xp_to_next : 100;
            string weapon = player != null && !string.IsNullOrEmpty(player.equipped_weapon) ? player.equipped_weapon : "Weathered Iron Axe";
            string armor = player != null && !string.IsNullOrEmpty(player.equipped_armor) ? player.equipped_armor : "Wanderer's Wool Cloak";

            var crestRect = new Rect(x + 12f, y + 12f, 54f, 54f);
            DrawFramedBox(crestRect, _texPanelDeep, _texGoldBorder, 1.5f);
            GUI.Label(new Rect(crestRect.x, crestRect.y + 6f, crestRect.width, 16f), "LVL", _lblCenterGold);
            GUI.Label(new Rect(crestRect.x, crestRect.y + 21f, crestRect.width, 28f), level.ToString(), _lblCenterWhite);

            float barX = x + 78f;
            float barW = panelW - 92f;

            float hpFrac = MaxHp > 0f ? Mathf.Clamp01(_displayHp / MaxHp) : 0f;
            DrawMeterBar(
                new Rect(barX, y + 14f, barW, 20f),
                hpFrac,
                _texHpFill,
                $"HP  {Mathf.CeilToInt(CurrentHp)} / {Mathf.CeilToInt(MaxHp)}"
            );

            float stmFrac = MaxStamina > 0f ? Mathf.Clamp01(_displayStamina / MaxStamina) : 0f;
            DrawMeterBar(
                new Rect(barX, y + 40f, barW, 17f),
                stmFrac,
                _texStaminaFill,
                $"STAMINA  {Mathf.CeilToInt(CurrentStamina)} / {Mathf.CeilToInt(MaxStamina)}"
            );

            float xpFrac = Mathf.Clamp01(_displayXp / xpNext);
            DrawMeterBar(
                new Rect(barX, y + 63f, barW, 12f),
                xpFrac,
                _texXpFill,
                $"XP {Mathf.RoundToInt(_displayXp)} / {xpNext}",
                smallText: true
            );

            GUI.DrawTexture(new Rect(x + 12f, y + 84f, panelW - 24f, 1f), _texIronBorder);

            GUI.Label(new Rect(x + 14f, y + 89f, panelW - 28f, 20f),
                $"<color=#d4a84f>WPN</color> {weapon}   <color=#78716c>|</color>   <color=#d4a84f>ARM</color> {armor}", _lblBody);

            string torchText = TorchLit ? "<color=#fbbf24>TORCH LIT</color>" : "<color=#78716c>TORCH OFF</color>";
            string modeText = IsFlyMode ? "<color=#7dd3fc>FLY</color>" : "<color=#86efac>WALK</color>";
            string swimText = IsSwimming ? "  ·  <color=#38bdf8>SWIMMING</color>" : string.Empty;
            string sunText = $"<color=#fde68a>SUN {SunElevationDeg:F0}°</color>";

            GUI.Label(new Rect(x + 14f, y + 114f, panelW - 28f, 20f),
                $"{torchText}  ·  {modeText}{swimText}  ·  {sunText}", _lblMuted);
        }

        private void DrawQuestTracker(float vw)
        {
            const float width = 376f;
            const float height = 148f;
            float x = vw - width - 22f;
            const float y = 18f;

            DrawFramedBox(new Rect(x, y, width, height), _texPanelDark, _texGoldBorder, 1f);

            var quest = rpgClient?.ActiveQuest;
            if (quest != null)
            {
                bool completed = rpgClient.IsQuestCompleted(quest.id);
                string header = completed ? "CONTRACT FULFILLED" : "ACTIVE CONTRACT";
                GUI.Label(new Rect(x + 14f, y + 10f, width - 28f, 18f), header, _lblSubtitle);
                GUI.Label(new Rect(x + 14f, y + 29f, width - 28f, 22f), quest.title, _lblTitle);

                float dist = rpgClient.DistanceToActiveQuest;
                string distStr = !float.IsNaN(dist) && !float.IsInfinity(dist)
                    ? $"{Mathf.RoundToInt(dist)}m"
                    : "--";
                string statusColor = rpgClient.IsNearActiveQuestTarget ? "#4ade80" : "#d4a84f";
                GUI.Label(new Rect(x + 14f, y + 51f, width - 28f, 18f),
                    $"<color=#a8a29e>{quest.giver}</color>   <color={statusColor}>◆ {distStr}</color>", _lblMuted);

                GUI.Label(new Rect(x + 14f, y + 71f, width - 28f, 38f), quest.ShortObjective, _lblBody);
            }
            else
            {
                GUI.Label(new Rect(x + 14f, y + 12f, width - 28f, 20f), "QUEST JOURNAL", _lblSubtitle);
                GUI.Label(new Rect(x + 14f, y + 36f, width - 28f, 44f), "No active contract selected.\nPress [J] to open the Codex.", _lblBody);
            }

            GUI.DrawTexture(new Rect(x + 12f, y + height - 34f, width - 24f, 1f), _texIronBorder);
            GUI.Label(new Rect(x + 12f, y + height - 30f, width - 24f, 26f),
                "<color=#cbd5e1>[J] Codex · [B] Campfire · [T] Torch · [V] Walk/Fly · [H] Hide HUD</color>", _lblHint);
        }

        private void DrawInteractionPrompt(float vw, float vh)
        {
            if (rpgClient == null || !rpgClient.CanCompleteActiveQuest || rpgClient.ActiveQuest == null)
                return;

            const float w = 410f;
            const float h = 42f;
            float x = (vw - w) * 0.5f;
            float y = vh - 120f;

            DrawFramedBox(new Rect(x, y, w, h), _texPanelDeep, _texGoldBorder, 1.5f);
            string label = $"<b><color=#facc15>[E]</color> Complete Quest: {rpgClient.ActiveQuest.title}</b> <color=#86efac>(+{rpgClient.ActiveQuest.reward_xp} XP)</color>";
            GUI.Label(new Rect(x + 10f, y + 9f, w - 20f, 24f), label, _lblCenterWhite);
        }

        private void DrawToastNotification(float vw)
        {
            if (rpgClient == null || string.IsNullOrEmpty(rpgClient.LastToastMessage))
                return;

            float age = Time.unscaledTime - rpgClient.LastToastTime;
            if (age < 0f || age > 3.6f) return;

            float alpha = age < 0.25f
                ? age / 0.25f
                : (age > 2.8f ? Mathf.Clamp01(1f - (age - 2.8f) / 0.8f) : 1f);

            const float w = 420f;
            const float h = 34f;
            float x = (vw - w) * 0.5f;
            const float y = 68f;

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            DrawFramedBox(new Rect(x, y, w, h), _texPanelDeep, _texGoldBorder, 1f);
            GUI.Label(new Rect(x + 12f, y + 7f, w - 24f, 20f), rpgClient.LastToastMessage, _lblCenterGold);
            GUI.color = prev;
        }

        private void DrawTelemetryBadge(float vw, float vh)
        {
            const float w = 390f;
            const float h = 58f;
            float x = vw - w - 22f;
            float y = vh - h - 22f;

            DrawFramedBox(new Rect(x, y, w, h), _texPanelDark, _texIronBorder, 1f);

            int fps = Mathf.RoundToInt(_fpsSmooth);
            int chunks = streamer != null ? streamer.ActiveChunkCount : 0;
            string tris = streamer != null ? streamer.ActiveTriangleCount.ToString("N0") : "0";
            string bakeMs = streamer?.LatestStats != null ? $"{streamer.LatestStats.avg_gpu_ms:F2} ms" : "--";
            string backend = streamer?.LatestStats != null
                ? $"{streamer.LatestStats.adapter} ({streamer.LatestStats.backend})"
                : (streamer != null ? streamer.ConnectionStatus : "Local");
            string rpgState = rpgClient != null && rpgClient.IsOnline ? "<color=#4ade80>RPG Sync</color>" : "<color=#fbbf24>Offline</color>";

            string line1 = $"<b><color=#f8fafc>{fps} FPS</color></b>  ·  Chunks: {chunks}  ·  Tris: {tris}  ·  Bake: {bakeMs}  ·  {rpgState}";
            string line2 = $"<color=#7dd3fc>{backend}</color>";

            GUI.Label(new Rect(x + 12f, y + 8f, w - 24f, 20f), line1, _lblTelemetry);
            GUI.Label(new Rect(x + 12f, y + 29f, w - 24f, 20f), line2, _lblTelemetry);
        }

        private void DrawCodexModal(float vw, float vh)
        {
            GUI.DrawTexture(new Rect(0f, 0f, vw, vh), _texDimOverlay);

            float w = Mathf.Min(920f, vw - 60f);
            float h = Mathf.Min(600f, vh - 60f);
            float x = (vw - w) * 0.5f;
            float y = (vh - h) * 0.5f;

            DrawFramedBox(new Rect(x, y, w, h), _texPanelDeep, _texGoldBorder, 1.5f);

            GUI.Label(new Rect(x + 20f, y + 14f, 460f, 26f), "NORTHERN CODEX  ·  CONTRACTS & SATCHEL", _lblTitle);

            if (rpgClient != null && GUI.Button(new Rect(x + w - 220f, y + 12f, 110f, 28f), "Sync Server", _btnStyle))
                rpgClient.RefreshState();

            if (GUI.Button(new Rect(x + w - 102f, y + 12f, 82f, 28f), "Close [J]", _btnStyle))
            {
                SetCodexOpen(false);
                return;
            }

            GUI.DrawTexture(new Rect(x + 16f, y + 48f, w - 32f, 1f), _texGoldBorder);

            var state = rpgClient?.CurrentState;
            var quests = state?.quests ?? Array.Empty<RpgQuest>();
            var player = state?.player;

            float leftW = 300f;
            float rightX = x + leftW + 32f;
            float rightW = w - leftW - 50f;
            float contentY = y + 60f;
            float contentH = h - 76f;

            GUI.Label(new Rect(x + 20f, contentY, leftW, 20f), "AVAILABLE & COMPLETED CONTRACTS", _lblSubtitle);
            _questListScroll = GUI.BeginScrollView(
                new Rect(x + 18f, contentY + 24f, leftW, contentH - 24f),
                _questListScroll,
                new Rect(0f, 0f, leftW - 20f, Mathf.Max(contentH - 24f, quests.Length * 62f))
            );

            RpgQuest selectedQuest = null;
            for (int i = 0; i < quests.Length; i++)
            {
                var q = quests[i];
                if (q == null) continue;
                bool isSelected = q.id == _selectedQuestId || (string.IsNullOrEmpty(_selectedQuestId) && i == 0);
                if (isSelected) selectedQuest = q;

                bool isActive = rpgClient?.ActiveQuest != null && rpgClient.ActiveQuest.id == q.id;
                bool isDone = rpgClient != null && rpgClient.IsQuestCompleted(q.id);

                string tag = isDone ? "<color=#4ade80>[DONE]</color>" : (isActive ? "<color=#facc15>[TRACKED]</color>" : "<color=#94a3b8>[OPEN]</color>");
                string btnLabel = $"{tag}  {q.title}\n<color=#a8a29e>{q.giver}  ·  +{q.reward_xp} XP</color>";

                if (GUI.Button(new Rect(0f, i * 60f, leftW - 22f, 54f), btnLabel, isSelected ? _btnSelectedStyle : _btnStyle))
                {
                    _selectedQuestId = q.id;
                    selectedQuest = q;
                }
            }
            GUI.EndScrollView();

            GUI.DrawTexture(new Rect(x + leftW + 24f, contentY, 1f, contentH), _texIronBorder);

            float detailsH = contentH * 0.64f;
            if (selectedQuest != null)
            {
                bool isDone = rpgClient != null && rpgClient.IsQuestCompleted(selectedQuest.id);
                bool isTracked = rpgClient?.ActiveQuest != null && rpgClient.ActiveQuest.id == selectedQuest.id;

                GUI.Label(new Rect(rightX, contentY, rightW, 24f), selectedQuest.title, _lblTitle);
                string rewardLabel = string.IsNullOrEmpty(selectedQuest.ItemRewardName)
                    ? $"+{selectedQuest.reward_xp} XP"
                    : $"+{selectedQuest.reward_xp} XP + {selectedQuest.ItemRewardName}";
                GUI.Label(new Rect(rightX, contentY + 24f, rightW, 18f),
                    $"Patron: {selectedQuest.giver}   ·   Reward: {rewardLabel}",
                    _lblSubtitle);

                string chronicleText = BuildQuestChronicle(selectedQuest);
                float loreBoxY = contentY + 48f;
                float loreBoxH = detailsH - 94f;
                float loreContentW = rightW - 38f;
                float loreContentH = Mathf.Max(loreBoxH - 12f, _lblBody.CalcHeight(new GUIContent(chronicleText), loreContentW) + 12f);

                DrawFramedBox(new Rect(rightX, loreBoxY, rightW, loreBoxH), _texPanelDark, _texIronBorder, 1f);
                _loreScroll = GUI.BeginScrollView(
                    new Rect(rightX + 8f, loreBoxY + 6f, rightW - 16f, loreBoxH - 12f),
                    _loreScroll,
                    new Rect(0f, 0f, loreContentW, loreContentH)
                );
                GUI.Label(new Rect(4f, 4f, loreContentW - 4f, loreContentH), chronicleText, _lblBody);
                GUI.EndScrollView();

                float btnY = contentY + detailsH - 38f;
                string trackText = isTracked ? "Currently Tracked" : "Track Quest";
                if (GUI.Button(new Rect(rightX, btnY, 160f, 30f), trackText, isTracked ? _btnSelectedStyle : _btnStyle))
                {
                    rpgClient?.SetActiveQuest(selectedQuest.id);
                }

                var tp = selectedQuest.TargetPosition;
                GUI.Label(new Rect(rightX + 172f, btnY + 5f, rightW - 172f, 22f),
                    $"Waypoint: ({tp.x:F0}, {tp.y:F0}, {tp.z:F0})   {(isDone ? "<color=#4ade80>Completed</color>" : "")}",
                    _lblMuted);
            }

            float invY = contentY + detailsH + 8f;
            float invH = contentH - detailsH - 8f;
            GUI.DrawTexture(new Rect(rightX, invY - 4f, rightW, 1f), _texIronBorder);
            GUI.Label(new Rect(rightX, invY, rightW, 20f),
                $"SATCHEL & GEAR   <color=#a8a29e>({rpgClient?.LastSyncStatus})</color>", _lblSubtitle);

            var inv = player?.inventory ?? Array.Empty<RpgInventoryItem>();
            float invBoxH = invH - 26f;
            DrawFramedBox(new Rect(rightX, invY + 24f, rightW, invBoxH), _texPanelDark, _texIronBorder, 1f);
            _inventoryScroll = GUI.BeginScrollView(
                new Rect(rightX + 8f, invY + 30f, rightW - 16f, invBoxH - 12f),
                _inventoryScroll,
                new Rect(0f, 0f, rightW - 36f, Mathf.Max(invBoxH - 12f, inv.Length * 26f))
            );

            if (inv.Length == 0)
            {
                GUI.Label(new Rect(6f, 6f, rightW - 40f, 24f), "Your satchel is empty.", _lblMuted);
            }
            else
            {
                for (int i = 0; i < inv.Length; i++)
                {
                    var item = inv[i];
                    if (item == null) continue;
                    GUI.Label(
                        new Rect(6f, i * 26f, rightW - 42f, 22f),
                        $"<b>{item.name}</b>  <color=#a8a29e>[{item.kind}]</color>  <color=#facc15>x{item.quantity}</color>",
                        _lblBody
                    );
                }
            }
            GUI.EndScrollView();
        }

        private static string BuildQuestChronicle(RpgQuest q)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(q.description))
            {
                sb.AppendLine(q.description);
                sb.AppendLine();
            }

            if (q.objectives != null && q.objectives.Length > 0)
            {
                sb.AppendLine("<color=#d4a84f><b>Objectives</b></color>");
                foreach (var obj in q.objectives)
                {
                    if (!string.IsNullOrEmpty(obj))
                        sb.AppendLine($"  ◆ {obj}");
                }
                sb.AppendLine();
            }

            if (q.lore != null && q.lore.Length > 0)
            {
                sb.AppendLine("<color=#d4a84f><b>World Lore</b></color>");
                foreach (var entry in q.lore)
                {
                    if (entry == null) continue;
                    if (!string.IsNullOrEmpty(entry.title))
                        sb.AppendLine($"<color=#fde68a><i>{entry.title}</i></color>");
                    if (!string.IsNullOrEmpty(entry.text))
                        sb.AppendLine(entry.text);
                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        private void DrawMeterBar(Rect rect, float fill01, Texture2D fillTex, string text, bool smallText = false)
        {
            DrawFramedBox(rect, _texBarTrack, _texIronBorder, 1f);
            if (fill01 > 0.001f)
            {
                var fillRect = new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * Mathf.Clamp01(fill01), rect.height - 4f);
                GUI.DrawTexture(fillRect, fillTex);
            }

            var style = smallText ? _lblCenterSmall : _lblCenterWhite;
            GUI.Label(new Rect(rect.x, rect.y - 1f, rect.width, rect.height + 2f), text, style);
        }

        private void DrawFramedBox(Rect rect, Texture2D fill, Texture2D border, float borderThickness)
        {
            GUI.DrawTexture(rect, fill);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, borderThickness), border);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - borderThickness, rect.width, borderThickness), border);
            GUI.DrawTexture(new Rect(rect.x, rect.y, borderThickness, rect.height), border);
            GUI.DrawTexture(new Rect(rect.xMax - borderThickness, rect.y, borderThickness, rect.height), border);
        }

        private void EnsureTexturesAndStyles()
        {
            if (_texPanelDark == null)
            {
                _texPanelDark = MakeVerticalGradient(new Color(0.06f, 0.07f, 0.09f, 0.86f), new Color(0.04f, 0.045f, 0.06f, 0.92f));
                _texPanelDeep = MakeVerticalGradient(new Color(0.08f, 0.085f, 0.11f, 0.94f), new Color(0.04f, 0.04f, 0.055f, 0.96f));
                _texGoldBorder = MakeSolid(new Color(0.78f, 0.62f, 0.30f, 0.72f));
                _texIronBorder = MakeSolid(new Color(0.36f, 0.39f, 0.44f, 0.55f));
                _texBarTrack = MakeSolid(new Color(0.03f, 0.03f, 0.04f, 0.92f));
                _texHpFill = MakeVerticalGradient(new Color(0.86f, 0.18f, 0.18f, 0.96f), new Color(0.55f, 0.07f, 0.09f, 0.96f));
                _texStaminaFill = MakeVerticalGradient(new Color(0.20f, 0.78f, 0.40f, 0.95f), new Color(0.08f, 0.46f, 0.22f, 0.95f));
                _texXpFill = MakeVerticalGradient(new Color(0.95f, 0.75f, 0.22f, 0.95f), new Color(0.68f, 0.45f, 0.08f, 0.95f));
                _texButtonNormal = MakeSolid(new Color(0.11f, 0.12f, 0.15f, 0.92f));
                _texButtonHover = MakeSolid(new Color(0.18f, 0.19f, 0.23f, 0.96f));
                _texButtonActive = MakeSolid(new Color(0.28f, 0.23f, 0.13f, 0.96f));
                _texDimOverlay = MakeSolid(new Color(0.02f, 0.025f, 0.035f, 0.68f));
            }

            if (_stylesBuilt) return;
            _stylesBuilt = true;

            Color parchment = new(0.94f, 0.90f, 0.82f);
            Color gold = new(0.86f, 0.69f, 0.34f);
            Color muted = new(0.74f, 0.72f, 0.68f);

            _lblTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                richText = true,
                normal = { textColor = parchment }
            };
            _lblSubtitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                richText = true,
                normal = { textColor = gold }
            };
            _lblBody = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                wordWrap = true,
                richText = true,
                normal = { textColor = parchment }
            };
            _lblMuted = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                richText = true,
                normal = { textColor = muted }
            };
            _lblCenterGold = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                normal = { textColor = gold }
            };
            _lblCenterWhite = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                normal = { textColor = parchment }
            };
            _lblCenterSmall = new GUIStyle(GUI.skin.label)
            {
                fontSize = 9,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                normal = { textColor = parchment }
            };
            _lblCompassTick = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = muted }
            };
            _lblCompassCard = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = parchment }
            };
            _lblTelemetry = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                richText = true,
                normal = { textColor = parchment }
            };
            _lblHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                richText = true,
                normal = { textColor = muted }
            };

            _btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                richText = true,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 6, 6),
                normal = { background = _texButtonNormal, textColor = parchment },
                hover = { background = _texButtonHover, textColor = Color.white },
                active = { background = _texButtonActive, textColor = gold }
            };
            _btnSelectedStyle = new GUIStyle(_btnStyle)
            {
                normal = { background = _texButtonActive, textColor = gold },
                hover = { background = _texButtonActive, textColor = Color.white }
            };
        }

        private static Texture2D MakeSolid(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeVerticalGradient(Color top, Color bottom)
        {
            const int h = 16;
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };
            for (int y = 0; y < h; y++)
                tex.SetPixel(0, y, Color.Lerp(bottom, top, y / (float)(h - 1)));
            tex.Apply();
            return tex;
        }

        private static void DestroyTex(ref Texture2D tex)
        {
            if (tex != null)
            {
                Destroy(tex);
                tex = null;
            }
        }

        private static (float deg, string label, bool major)[] BuildCompassTicks()
        {
            string[] cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            var list = new (float deg, string label, bool major)[24];
            for (int i = 0; i < 24; i++)
            {
                float deg = i * 15f;
                bool major = i % 3 == 0;
                string label = major ? cardinals[i / 3] : ((int)deg).ToString();
                list[i] = (deg, label, major);
            }
            return list;
        }
    }
}
