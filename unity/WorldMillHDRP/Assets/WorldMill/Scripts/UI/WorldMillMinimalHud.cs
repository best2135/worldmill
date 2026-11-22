using System;
using System.Collections.Generic;
using UnityEngine;
using WorldMill;
// WorldMill.UI also declares RpgQuest / RpgInventoryItem / RpgLoreEntry (legacy server client),
// so the local store's types are aliased explicitly to avoid resolving the wrong ones.
using StoreQuest = WorldMill.RpgQuest;
using StoreItem = WorldMill.RpgInventoryItem;
using StoreLore = WorldMill.RpgLoreEntry;

namespace WorldMill.UI
{
    /// <summary>
    /// Restrained, mostly-empty RPG HUD for the local (serverless) WorldMill RPG.
    /// Mirrors docs/ui-design-system.html and docs/ui-tokens.json (v2, "restraint pass").
    ///
    ///   Top-center     hairline compass, N/E/S/W, one gold diamond + metres for the active quest
    ///   Top-center     quiet toast under the compass when rpgStore.ToastMessage changes
    ///   Top-right      borderless quest title + one objective + distance (drop-shadowed)
    ///   Bottom-left    thin HP / stamina / XP bars with trailing drain, "Lv. n" + weapon
    ///   Bottom-center  1px framed "[E] Complete — Quest" prompt when in range
    ///   J              editorial journal: quests, chronicle (lore), inventory, Track
    ///   H              hide every HUD element for screenshots
    ///
    /// Gold (#d4a853) is reserved for attention targets: the quest diamond (compass, tracker,
    /// journal row) and the XP hairline (dimmed). Everything else is ink on dark glass.
    /// Pure IMGUI: only UnityEngine + System, no assets, no UI packages.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldMillMinimalHud : MonoBehaviour
    {
        [Header("References (auto-resolved when empty)")]
        public WorldMillLocalRpgStore rpgStore;
        public Transform playerTransform;

        [Header("Vitals (push via SetVitals)")]
        public float CurrentHp = 100f, MaxHp = 100f, CurrentStamina = 100f, MaxStamina = 100f;

        [Header("Player state")]
        public bool TorchLit, IsSwimming, IsFlyMode;
        [Tooltip("Higher sun = brighter sky = stronger text shadow for legibility.")]
        public float SunElevationDeg = 28f;

        [Header("Keys")]
        public KeyCode journalKey = KeyCode.J;
        public KeyCode hideHudKey = KeyCode.H;

        public bool IsCodexOpen { get; private set; }
        public bool IsHudVisible => _hudVisible;

        public void SetVitals(float hp, float maxHp, float stamina, float maxStamina)
        {
            MaxHp = Mathf.Max(1f, maxHp);
            MaxStamina = Mathf.Max(1f, maxStamina);
            CurrentHp = Mathf.Clamp(hp, 0f, MaxHp);
            CurrentStamina = Mathf.Clamp(stamina, 0f, MaxStamina);
        }

        // ------------------------------------------------------------------ tokens
        // Mirrors ui-tokens.json → color.*  (sRGB, assign as-is in a linear HDRP project)
        private static readonly Color Ink      = new Color(0.910f, 0.886f, 0.831f, 1f); // #e8e2d4
        private static readonly Color Gold     = new Color(0.831f, 0.659f, 0.325f, 1f); // #d4a853
        private static readonly Color Hp       = new Color(0.608f, 0.180f, 0.165f, 1f); // #9b2e2a
        private static readonly Color Stamina  = new Color(0.247f, 0.463f, 0.349f, 1f); // #3f7659
        private static readonly Color Surface  = new Color(0.047f, 0.055f, 0.071f, 1f); // #0c0e12
        private static readonly Color Shadow   = new Color(0f, 0f, 0f, 1f);

        private const float InkStrong = 0.94f, InkMuted = 0.72f, InkFaint = 0.48f;
        private const float HairlineA = 0.16f, HairlineStrongA = 0.32f;

        // ui-tokens.json → motion.*
        private const float TrailDelay = 0.45f;   // s before the drain trail starts to fall
        private const float TrailMinRate = 0.18f; // fraction / s
        private const float HealRate = 0.9f;      // fraction / s
        private const float ToastIn = 0.25f, ToastHold = 3.2f, ToastOut = 0.8f;
        private const float FadeFast = 0.18f, FadeSheet = 0.22f, FadeHud = 0.25f;

        // ------------------------------------------------------------------ state
        private bool _hudVisible = true;
        private float _hudAlpha = 1f, _codexAlpha, _promptAlpha;

        private float _hpFill = 1f, _hpTrail = 1f, _hpHold, _hpPrev = 1f;
        private float _stFill = 1f, _stTrail = 1f, _stHold, _stPrev = 1f;
        private float _xpFill;
        private int _xpLevelSeen = -1;

        private float _scale = 1f, _px = 1f;
        private Texture2D _fadeH, _diamond;
        private GUIStyle _sans, _serif;
        private bool _stylesReady;
        private readonly GUIContent _gc = new GUIContent();

        private enum JournalTab { Quests, Chronicle, Inventory }
        private JournalTab _tab = JournalTab.Quests;
        private string _selQuestId, _selItemId;
        private int _selLore;
        private readonly List<(StoreLore lore, StoreQuest quest)> _loreBuf = new();

        private bool _savedCursor;
        private CursorLockMode _prevLock;
        private bool _prevVisible;

        private int _distCacheKey = int.MinValue;
        private string _distCache = "";

        // ------------------------------------------------------------------ lifecycle
        private void Awake()
        {
            _fadeH = BuildFadeTexture();
            _diamond = BuildDiamondTexture();
            float hp01 = Ratio(CurrentHp, MaxHp), st01 = Ratio(CurrentStamina, MaxStamina);
            _hpFill = _hpTrail = _hpPrev = hp01;
            _stFill = _stTrail = _stPrev = st01;
        }

        private void OnDisable() => RestoreCursor();

        private void OnDestroy()
        {
            if (_fadeH != null) Destroy(_fadeH);
            if (_diamond != null) Destroy(_diamond);
        }

        private void Update()
        {
            if (rpgStore == null) rpgStore = FindFirstObjectByType<WorldMillLocalRpgStore>();
            if (playerTransform == null && rpgStore != null) playerTransform = rpgStore.playerTransform;

            if (Input.GetKeyDown(hideHudKey))
            {
                _hudVisible = !_hudVisible;
                if (!_hudVisible) SetCodex(false);
            }
            if (_hudVisible && Input.GetKeyDown(journalKey)) SetCodex(!IsCodexOpen);
            else if (IsCodexOpen && Input.GetKeyDown(KeyCode.Escape)) SetCodex(false);

            float dt = Time.unscaledDeltaTime;
            _hudAlpha = Mathf.MoveTowards(_hudAlpha, _hudVisible ? 1f : 0f, dt / FadeHud);
            _codexAlpha = Mathf.MoveTowards(_codexAlpha, IsCodexOpen ? 1f : 0f, dt / FadeSheet);
            bool promptWanted = rpgStore != null && rpgStore.CanCompleteActiveQuest && rpgStore.ActiveQuest != null && !IsCodexOpen;
            _promptAlpha = Mathf.MoveTowards(_promptAlpha, promptWanted ? 1f : 0f, dt / FadeFast);

            StepBar(ref _hpFill, ref _hpTrail, ref _hpHold, ref _hpPrev, Ratio(CurrentHp, MaxHp), dt);
            StepBar(ref _stFill, ref _stTrail, ref _stHold, ref _stPrev, Ratio(CurrentStamina, MaxStamina), dt);

            var save = rpgStore != null ? rpgStore.Save : null;
            if (save != null)
            {
                float xp01 = save.xpToNext > 0 ? Mathf.Clamp01(save.xp / (float)save.xpToNext) : 0f;
                if (_xpLevelSeen != save.level) { _xpLevelSeen = save.level; _xpFill = Mathf.Min(_xpFill, xp01); }
                _xpFill = Mathf.MoveTowards(_xpFill, xp01, dt * 0.6f);
            }
        }

        /// Elden-Ring style: fill drops instantly, a pale trail holds, then drains toward it.
        private static void StepBar(ref float fill, ref float trail, ref float hold, ref float prev, float target, float dt)
        {
            if (prev - target > 0.015f) hold = TrailDelay;   // discrete hit (not a continuous sprint drain)
            prev = target;

            if (target < fill) fill = target;
            else fill = Mathf.MoveTowards(fill, target, dt * HealRate);

            if (trail <= fill) { trail = fill; hold = 0f; return; }
            hold -= dt;
            if (hold <= 0f) trail = Mathf.Max(fill, trail - Mathf.Max((trail - fill) * 3.5f, TrailMinRate) * dt);
        }

        private void SetCodex(bool open)
        {
            if (open == IsCodexOpen) return;
            IsCodexOpen = open;
            if (open)
            {
                if (!_savedCursor) { _prevLock = Cursor.lockState; _prevVisible = Cursor.visible; _savedCursor = true; }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                var aq = rpgStore != null ? rpgStore.ActiveQuest : null;
                if (aq != null) _selQuestId = aq.id;
            }
            else RestoreCursor();
        }

        private void RestoreCursor()
        {
            if (!_savedCursor) return;
            Cursor.lockState = _prevLock;
            Cursor.visible = _prevVisible;
            _savedCursor = false;
        }

        // ------------------------------------------------------------------ OnGUI
        private void OnGUI()
        {
            if (_hudAlpha <= 0.001f && _codexAlpha <= 0.001f) return;
            EnsureStyles();
            _scale = Mathf.Max(0.6f, Screen.height / 1080f);
            _px = Mathf.Max(1f, Mathf.Round(_scale));
            GUI.depth = -20;

            float a = _hudAlpha;
            float hudUnderSheet = a * (1f - 0.85f * _codexAlpha);
            DrawCompass(hudUnderSheet);
            DrawTracker(hudUnderSheet);
            DrawVitals(hudUnderSheet);
            DrawToast(a);
            DrawPrompt(a * _promptAlpha);
            if (_codexAlpha > 0.001f) DrawJournal(_codexAlpha * a);
        }

        // ------------------------------------------------------------------ compass
        private void DrawCompass(float a)
        {
            if (a <= 0.001f) return;
            float w = S(560f), cx = Mathf.Round(Screen.width * 0.5f), y = Mathf.Round(S(44f));
            float half = w * 0.5f;
            float heading = HeadingDeg();

            Tex(new Rect(cx - half, y, w, _px), _fadeH, WithA(Ink, 0.34f * a));
            Fill(new Rect(cx, y + _px, _px, Mathf.Round(S(4f))), WithA(Ink, 0.45f * a)); // heading notch

            for (int deg = 0; deg < 360; deg += 15)
            {
                float d = Mathf.DeltaAngle(heading, deg);
                if (Mathf.Abs(d) > 88f) continue;
                float x = Mathf.Round(cx + d / 90f * half);
                float edge = 1f - Mathf.SmoothStep(0.55f, 1f, Mathf.Abs(d) / 90f);
                bool cardinal = deg % 90 == 0;
                float th = Mathf.Round(S(cardinal ? 6f : 3f));
                Fill(new Rect(x, y - th, _px, th), WithA(Ink, (cardinal ? 0.62f : 0.24f) * edge * a));
                if (cardinal)
                {
                    string l = deg == 0 ? "N" : deg == 90 ? "E" : deg == 180 ? "S" : "W";
                    _sans.fontSize = Fs(12f);
                    _sans.fontStyle = FontStyle.Normal;
                    float lw = Measure(_sans, l);
                    Text(new Rect(x - lw * 0.5f, y - S(24f), lw + 2f, S(16f)), l, _sans,
                        WithA(Ink, (deg == 0 ? 0.92f : 0.66f) * edge * a), ShadowStrength());
                }
            }

            var q = rpgStore != null ? rpgStore.ActiveQuest : null;
            if (q == null || q.completed) return;
            Vector3 from = PlayerPos();
            Vector3 to = q.targetPosition - from;
            float bearing = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float dq = Mathf.DeltaAngle(heading, bearing);
            bool offscreen = Mathf.Abs(dq) > 88f;
            float qx = Mathf.Round(cx + Mathf.Clamp(dq, -88f, 88f) / 90f * half);
            float qa = (offscreen ? 0.55f : 1f) * a;

            float ds = Mathf.Round(S(11f));
            Tex(new Rect(qx - ds * 0.5f - _px, y - ds * 0.5f - _px, ds + 2f * _px, ds + 2f * _px), _diamond, WithA(Shadow, 0.6f * qa));
            Tex(new Rect(qx - ds * 0.5f, y - ds * 0.5f, ds, ds), _diamond, WithA(Gold, qa));

            string dist = DistanceLabel(QuestDistance(q));
            _sans.fontSize = Fs(11f);
            float dw = Measure(_sans, dist);
            Text(new Rect(qx - dw * 0.5f, y + S(9f), dw + 2f, S(16f)), dist, _sans, WithA(Ink, InkMuted * qa), ShadowStrength());
        }

        // ------------------------------------------------------------------ tracker
        private void DrawTracker(float a)
        {
            var q = rpgStore != null ? rpgStore.ActiveQuest : null;
            if (a <= 0.001f || q == null || q.completed) return;

            float right = Screen.width - S(44f), top = S(32f);
            float sh = ShadowStrength();

            _serif.fontSize = Fs(18f);
            float tw = Measure(_serif, q.title);
            Text(new Rect(right - tw, top, tw + 2f, S(26f)), q.title, _serif, WithA(Ink, InkStrong * a), sh);
            float ds = Mathf.Round(S(7f));
            Tex(new Rect(Mathf.Round(right - tw - S(10f) - ds), Mathf.Round(top + S(13f) - ds * 0.5f), ds, ds), _diamond, WithA(Gold, a));

            string obj = CurrentObjective(q);
            string dist = DistanceLabel(QuestDistance(q));
            _sans.fontSize = Fs(13f);
            float dw = Measure(_sans, dist);
            Text(new Rect(right - dw, top + S(27f), dw + 2f, S(20f)), dist, _sans, WithA(Ink, InkFaint * a), sh);
            if (!string.IsNullOrEmpty(obj))
            {
                float ow = Mathf.Min(Measure(_sans, obj), S(420f));
                Text(new Rect(right - dw - S(12f) - ow, top + S(27f), ow + 2f, S(20f)), obj, _sans, WithA(Ink, InkMuted * a), sh);
            }
        }

        private string CurrentObjective(StoreQuest q)
        {
            if (q.objectives == null || q.objectives.Length == 0) return "";
            bool near = rpgStore.CanCompleteActiveQuest || QuestDistance(q) <= rpgStore.questCompletionRadius * 4f;
            return q.objectives[near ? q.objectives.Length - 1 : 0];
        }

        // ------------------------------------------------------------------ vitals
        private void DrawVitals(float a)
        {
            if (a <= 0.001f) return;
            float x = Mathf.Round(S(44f));
            float bottom = Screen.height - S(40f);
            float sh = ShadowStrength();

            float hpW = Mathf.Round(S(Mathf.Clamp(280f + (MaxHp - 100f) * 1.2f, 180f, 460f)));
            float stW = Mathf.Round(S(Mathf.Clamp(220f + (MaxStamina - 100f) * 1.2f, 140f, 380f)));
            float hpH = Mathf.Max(4f, Mathf.Round(S(7f)));
            float stH = Mathf.Max(3f, Mathf.Round(S(5f)));
            float xpH = Mathf.Max(1f, Mathf.Round(S(2f)));

            // Bottom text row: "Lv. 1   Iron Greatsword   · torch lit"
            var save = rpgStore != null ? rpgStore.Save : null;
            _sans.fontSize = Fs(12f);
            float rowH = S(16f);
            float rowY = Mathf.Round(bottom - rowH);
            string lvl = save != null ? "Lv. " + save.level : "Lv. 1";
            float lw = Measure(_sans, lvl);
            Text(new Rect(x, rowY, lw + 2f, rowH), lvl, _sans, WithA(Ink, InkMuted * a), sh);
            float cursor = x + lw + S(14f);
            string weapon = save != null ? save.equippedWeapon : null;
            if (!string.IsNullOrEmpty(weapon))
            {
                float ww = Measure(_sans, weapon);
                Text(new Rect(cursor, rowY, ww + 2f, rowH), weapon, _sans, WithA(Ink, InkFaint * a), sh);
                cursor += ww + S(14f);
            }
            string state = IsFlyMode ? "Fly mode" : IsSwimming ? "Swimming" : TorchLit ? "Torch lit" : null;
            if (state != null)
            {
                float sw = Measure(_sans, state);
                Text(new Rect(cursor, rowY, sw + 2f, rowH), state, _sans, WithA(Ink, InkFaint * 0.85f * a), sh);
            }

            float xpY = Mathf.Round(rowY - S(9f) - xpH);
            float stY = Mathf.Round(xpY - S(6f) - stH);
            float hpY = Mathf.Round(stY - S(5f) - hpH);

            float hp01 = Ratio(CurrentHp, MaxHp);
            float st01 = Ratio(CurrentStamina, MaxStamina);
            float t = Time.unscaledTime;
            float lowHpPulse = hp01 < 0.25f ? 0.82f + 0.18f * Mathf.Sin(t * 5.2f) : 1f;
            float lowStPulse = st01 < 0.15f ? 0.6f + 0.4f * (0.5f + 0.5f * Mathf.Sin(t * 5.7f)) : 1f;

            Bar(new Rect(x, hpY, hpW, hpH), _hpFill, _hpTrail, Hp * new Color(lowHpPulse, lowHpPulse, lowHpPulse, 1f), WithA(Ink, 0.5f), a);
            Bar(new Rect(x, stY, stW, stH), _stFill, _stTrail, Stamina, WithA(Ink, 0.32f), a * lowStPulse);

            // XP: a hairline, no frame. Dimmed gold — the only passive gold on screen.
            Fill(new Rect(x, xpY, hpW, xpH), WithA(Ink, 0.10f * a));
            Fill(new Rect(x, xpY, Mathf.Round(hpW * _xpFill), xpH), WithA(Gold, 0.72f * a));
        }

        private void Bar(Rect r, float fill, float trail, Color fillCol, Color trailCol, float a)
        {
            r = Snap(r);
            Fill(new Rect(r.x - _px, r.y - _px, r.width + 2f * _px, r.height + 2f * _px), WithA(Shadow, 0.78f * a)); // 1px dark border
            Fill(r, WithA(Surface, 0.55f * a));                                                                  // track
            float fw = Mathf.Round(r.width * Mathf.Clamp01(fill));
            float tw = Mathf.Round(r.width * Mathf.Clamp01(trail));
            if (tw > fw) Fill(new Rect(r.x + fw, r.y, tw - fw, r.height), WithA(trailCol, trailCol.a * a));
            if (fw > 0f)
            {
                Fill(new Rect(r.x, r.y, fw, r.height), WithA(fillCol, a));
                Fill(new Rect(r.x, r.y, fw, _px), WithA(Color.white, 0.10f * a)); // top light catch
            }
        }

        // ------------------------------------------------------------------ toast
        private void DrawToast(float a)
        {
            if (a <= 0.001f || rpgStore == null || string.IsNullOrEmpty(rpgStore.ToastMessage)) return;
            float age = Time.unscaledTime - rpgStore.ToastTimestamp;
            if (age < 0f || age > ToastIn + ToastHold + ToastOut) return;
            float env = age < ToastIn ? Mathf.SmoothStep(0f, 1f, age / ToastIn)
                      : age < ToastIn + ToastHold ? 1f
                      : 1f - Mathf.SmoothStep(0f, 1f, (age - ToastIn - ToastHold) / ToastOut);
            float ta = env * a;
            float rise = (1f - Mathf.Clamp01(age / ToastIn)) * S(4f);

            string msg = rpgStore.ToastMessage;
            _sans.fontSize = Fs(14f);
            float w = Measure(_sans, msg);
            float cx = Screen.width * 0.5f, y = Mathf.Round(S(84f) + rise);
            Text(new Rect(cx - w * 0.5f, y, w + 2f, S(20f)), msg, _sans, WithA(Ink, InkStrong * ta), ShadowStrength());
            float rule = Mathf.Round(S(28f)), gap = S(14f), my = Mathf.Round(y + S(10f));
            Fill(new Rect(Mathf.Round(cx - w * 0.5f - gap - rule), my, rule, _px), WithA(Ink, 0.30f * ta));
            Fill(new Rect(Mathf.Round(cx + w * 0.5f + gap), my, rule, _px), WithA(Ink, 0.30f * ta));
        }

        // ------------------------------------------------------------------ prompt
        private void DrawPrompt(float a)
        {
            var q = rpgStore != null ? rpgStore.ActiveQuest : null;
            if (a <= 0.001f || q == null) return;

            string label = "Complete — " + q.title;
            _sans.fontSize = Fs(14f);
            float lw = Measure(_sans, label);
            float key = Mathf.Round(S(22f)), padX = S(14f), padY = S(9f), gap = S(12f);
            float w = Mathf.Round(padX + key + gap + lw + padX), h = Mathf.Round(key + padY * 2f);
            float x = Mathf.Round(Screen.width * 0.5f - w * 0.5f), y = Mathf.Round(Screen.height - S(150f) - h);
            var r = new Rect(x, y, w, h);

            Fill(r, WithA(Surface, 0.58f * a));
            Frame(r, WithA(Ink, HairlineStrongA * 0.75f * a));
            var k = new Rect(Mathf.Round(x + padX), Mathf.Round(y + padY), key, key);
            Frame(k, WithA(Ink, 0.55f * a));
            _sans.fontSize = Fs(12f);
            float kw = Measure(_sans, "E");
            Text(new Rect(k.x + (key - kw) * 0.5f, k.y + (key - S(16f)) * 0.5f, kw + 2f, S(16f)), "E", _sans, WithA(Ink, InkStrong * a), 0f);
            _sans.fontSize = Fs(14f);
            Text(new Rect(k.xMax + gap, y + (h - S(20f)) * 0.5f, lw + 2f, S(20f)), label, _sans, WithA(Ink, InkStrong * a), 0f);
        }

        // ------------------------------------------------------------------ journal
        private void DrawJournal(float a)
        {
            Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.024f, 0.027f, 0.035f, 0.62f * a));

            float w = Mathf.Round(Mathf.Min(S(1040f), Screen.width - S(96f)));
            float h = Mathf.Round(Mathf.Min(S(640f), Screen.height - S(96f)));
            float lift = (1f - a) * S(10f);
            var sheet = new Rect(Mathf.Round((Screen.width - w) * 0.5f), Mathf.Round((Screen.height - h) * 0.5f + lift), w, h);
            Fill(sheet, WithA(Surface, 0.90f * a));
            Frame(sheet, WithA(Ink, 0.14f * a));

            float pad = S(40f);
            float innerX = sheet.x + pad, innerW = sheet.width - pad * 2f;
            float top = sheet.y + S(30f);

            // Header: "Journal" + quiet text tabs
            _serif.fontSize = Fs(26f);
            Text(new Rect(innerX, top, S(300f), S(36f)), "Journal", _serif, WithA(Ink, InkStrong * a), 0f);

            float tabRight = innerX + innerW;
            _sans.fontSize = Fs(13f);
            for (int i = 2; i >= 0; i--)
            {
                var tab = (JournalTab)i;
                string tl = tab == JournalTab.Quests ? "Quests" : tab == JournalTab.Chronicle ? "Chronicle" : "Inventory";
                float tw = Measure(_sans, tl);
                var tr = new Rect(tabRight - tw, top + S(10f), tw, S(22f));
                bool on = _tab == tab, hov = Hover(tr);
                Text(tr, tl, _sans, WithA(Ink, (on ? InkStrong : hov ? 0.78f : InkFaint) * a), 0f);
                if (on) Fill(new Rect(tr.x, Mathf.Round(tr.yMax + S(2f)), tw, _px), WithA(Ink, 0.7f * a));
                if (Click(tr)) _tab = tab;
                tabRight -= tw + S(28f);
            }

            float ruleY = Mathf.Round(top + S(52f));
            Fill(new Rect(innerX, ruleY, innerW, _px), WithA(Ink, 0.10f * a));

            float bodyY = ruleY + S(24f);
            float footY = sheet.yMax - S(42f);
            float bodyH = footY - bodyY - S(12f);
            float leftW = Mathf.Round(Mathf.Min(S(300f), innerW * 0.36f));
            var left = new Rect(innerX, bodyY, leftW, bodyH);
            var right = new Rect(innerX + leftW + S(40f), bodyY, innerW - leftW - S(40f), bodyH);
            Fill(new Rect(Mathf.Round(innerX + leftW + S(20f)), bodyY, _px, bodyH), WithA(Ink, 0.07f * a));

            switch (_tab)
            {
                case JournalTab.Quests: DrawQuestsTab(left, right, a); break;
                case JournalTab.Chronicle: DrawChronicleTab(left, right, a); break;
                default: DrawInventoryTab(left, right, a); break;
            }

            // Footer
            _sans.fontSize = Fs(12f);
            Text(new Rect(innerX, footY, S(300f), S(18f)), "J or Esc to close", _sans, WithA(Ink, InkFaint * a), 0f);
            var save = rpgStore != null ? rpgStore.Save : null;
            if (save != null)
            {
                string xp = "Lv. " + save.level + "  ·  " + save.xp + " / " + save.xpToNext + " XP";
                float xw = Measure(_sans, xp);
                Text(new Rect(innerX + innerW - xw, footY, xw + 2f, S(18f)), xp, _sans, WithA(Ink, InkFaint * a), 0f);
            }
        }

        private void DrawQuestsTab(Rect left, Rect right, float a)
        {
            var quests = rpgStore != null ? rpgStore.Quests : null;
            if (quests == null || quests.Count == 0) { EmptyNote(left, "No quests yet", a); return; }

            StoreQuest sel = null;
            for (int i = 0; i < quests.Count; i++) if (quests[i].id == _selQuestId) sel = quests[i];
            if (sel == null) { sel = rpgStore.ActiveQuest ?? quests[0]; _selQuestId = sel.id; }

            float y = left.y, rowH = S(58f);
            for (int i = 0; i < quests.Count; i++)
            {
                var q = quests[i];
                bool active = rpgStore.ActiveQuest == q && !q.completed;
                string sub = q.completed ? "Completed" : active ? "Tracking  ·  " + q.giver : q.giver;
                if (ListRow(new Rect(left.x, y, left.width, rowH), q.title, sub, q.id == _selQuestId, active, q.completed, a))
                    _selQuestId = q.id;
                y += rowH + S(4f);
            }

            // Detail column
            float x = right.x, w = Mathf.Min(right.width, S(580f)), dy = right.y;
            _serif.fontSize = Fs(24f);
            Text(new Rect(x, dy - S(4f), w, S(32f)), sel.title, _serif, WithA(Ink, InkStrong * a), 0f);

            // Track control, right-aligned on the title line
            _sans.fontSize = Fs(13f);
            bool tracked = rpgStore.ActiveQuest == sel;
            if (sel.completed || tracked)
            {
                string s = sel.completed ? "Completed" : "Tracking";
                float sw = Measure(_sans, s);
                Text(new Rect(right.xMax - sw, dy + S(4f), sw + 2f, S(18f)), s, _sans, WithA(Ink, InkFaint * a), 0f);
            }
            else
            {
                const string label = "Track";
                float bw = Mathf.Round(Measure(_sans, label) + S(28f)), bh = Mathf.Round(S(28f));
                var br = new Rect(Mathf.Round(right.xMax - bw), Mathf.Round(dy - S(1f)), bw, bh);
                bool hov = Hover(br);
                if (hov) Fill(br, WithA(Ink, 0.06f * a));
                Frame(br, WithA(Ink, (hov ? 0.55f : HairlineStrongA) * a));
                float lw = Measure(_sans, label);
                Text(new Rect(br.x + (bw - lw) * 0.5f, br.y + (bh - S(18f)) * 0.5f, lw + 2f, S(18f)), label, _sans, WithA(Ink, InkStrong * a), 0f);
                if (Click(br)) rpgStore.SetActiveQuest(sel.id);
            }
            dy += S(38f);

            _sans.fontSize = Fs(12f);
            string meta = "Given by " + sel.giver + "  ·  " + sel.rewardXp + " XP" +
                          (sel.rewardItem != null ? "  ·  " + sel.rewardItem.name : "");
            Text(new Rect(x, dy, w, S(18f)), meta, _sans, WithA(Ink, InkFaint * a), 0f);
            dy += S(30f);

            dy = Paragraph(x, dy, w, sel.description, 14f, InkMuted, a) + S(22f);

            dy = Label(x, dy, "Objectives", a);
            if (sel.objectives != null)
            {
                for (int i = 0; i < sel.objectives.Length; i++)
                {
                    float oa = sel.completed ? 0.45f : InkMuted;
                    Fill(new Rect(x, Mathf.Round(dy + S(9f)), Mathf.Round(S(8f)), _px), WithA(Ink, 0.4f * a));
                    dy = Paragraph(x + S(18f), dy, w - S(18f), sel.objectives[i], 13f, oa, a) + S(6f);
                }
            }
            dy += S(16f);

            if (sel.lore != null && sel.lore.Length > 0 && dy < right.yMax - S(60f))
            {
                dy = Label(x, dy, "Chronicle", a);
                for (int i = 0; i < sel.lore.Length && dy < right.yMax - S(40f); i++)
                {
                    _serif.fontSize = Fs(15f);
                    Text(new Rect(x, dy, w, S(22f)), sel.lore[i].title, _serif, WithA(Ink, 0.86f * a), 0f);
                    dy = Paragraph(x, dy + S(22f), w, sel.lore[i].text, 13f, InkFaint + 0.1f, a) + S(10f);
                }
            }
        }

        private void DrawChronicleTab(Rect left, Rect right, float a)
        {
            _loreBuf.Clear();
            int hidden = 0;
            var quests = rpgStore != null ? rpgStore.Quests : null;
            if (quests != null)
            {
                foreach (var q in quests)
                {
                    if (q.lore == null) continue;
                    bool known = q.completed || rpgStore.ActiveQuest == q;
                    foreach (var l in q.lore) { if (known) _loreBuf.Add((l, q)); else hidden++; }
                }
            }
            if (_loreBuf.Count == 0) { EmptyNote(left, "Nothing recorded yet", a); return; }
            _selLore = Mathf.Clamp(_selLore, 0, _loreBuf.Count - 1);

            float y = left.y, rowH = S(58f);
            for (int i = 0; i < _loreBuf.Count; i++)
            {
                if (ListRow(new Rect(left.x, y, left.width, rowH), _loreBuf[i].lore.title, _loreBuf[i].quest.title, i == _selLore, false, false, a))
                    _selLore = i;
                y += rowH + S(4f);
            }
            if (hidden > 0)
            {
                _sans.fontSize = Fs(12f);
                Text(new Rect(left.x + S(14f), y + S(8f), left.width, S(18f)),
                    hidden == 1 ? "1 entry undiscovered" : hidden + " entries undiscovered", _sans, WithA(Ink, InkFaint * 0.8f * a), 0f);
            }

            var sel = _loreBuf[_selLore];
            float x = right.x, w = Mathf.Min(right.width, S(580f)), dy = right.y;
            _serif.fontSize = Fs(24f);
            Text(new Rect(x, dy - S(4f), w, S(32f)), sel.lore.title, _serif, WithA(Ink, InkStrong * a), 0f);
            dy += S(38f);
            _sans.fontSize = Fs(12f);
            Text(new Rect(x, dy, w, S(18f)), "From " + sel.quest.title, _sans, WithA(Ink, InkFaint * a), 0f);
            dy += S(30f);
            Paragraph(x, dy, w, sel.lore.text, 15f, InkMuted + 0.06f, a, _serif);
        }

        private void DrawInventoryTab(Rect left, Rect right, float a)
        {
            var save = rpgStore != null ? rpgStore.Save : null;
            var items = save != null ? save.inventory : null;
            if (items == null || items.Count == 0) { EmptyNote(left, "Nothing carried", a); return; }

            StoreItem sel = null;
            for (int i = 0; i < items.Count; i++) if (items[i].id == _selItemId) sel = items[i];
            if (sel == null) { sel = items[0]; _selItemId = sel.id; }

            float y = left.y, rowH = S(50f);
            for (int i = 0; i < items.Count && y + rowH <= left.yMax + S(4f); i++)
            {
                var it = items[i];
                bool equipped = it.name == save.equippedWeapon || it.name == save.equippedArmor;
                string title = it.quantity > 1 ? it.name + "  ×" + it.quantity : it.name;
                string sub = SentenceCase(it.kind) + (equipped ? "  ·  Equipped" : "");
                if (ListRow(new Rect(left.x, y, left.width, rowH), title, sub, it.id == _selItemId, false, false, a))
                    _selItemId = it.id;
                y += rowH + S(4f);
            }

            float x = right.x, w = Mathf.Min(right.width, S(580f)), dy = right.y;
            _serif.fontSize = Fs(24f);
            Text(new Rect(x, dy - S(4f), w, S(32f)), sel.name, _serif, WithA(Ink, InkStrong * a), 0f);
            dy += S(38f);
            bool eq = sel.name == save.equippedWeapon || sel.name == save.equippedArmor;
            _sans.fontSize = Fs(12f);
            string meta = SentenceCase(sel.kind) + (sel.quantity > 1 ? "  ·  " + sel.quantity + " carried" : "") + (eq ? "  ·  Equipped" : "");
            Text(new Rect(x, dy, w, S(18f)), meta, _sans, WithA(Ink, InkFaint * a), 0f);
            dy += S(30f);
            Paragraph(x, dy, w, sel.description, 14f, InkMuted, a);
        }

        /// Quiet list row. Selected: 1px ink rule on the left + faint wash. Tracked: gold diamond.
        private bool ListRow(Rect r, string title, string sub, bool selected, bool tracked, bool done, float a)
        {
            r = Snap(r);
            bool hov = Hover(r);
            if (selected) { Fill(r, WithA(Ink, 0.045f * a)); Fill(new Rect(r.x, r.y, _px, r.height), WithA(Ink, 0.7f * a)); }
            else if (hov) Fill(r, WithA(Ink, 0.025f * a));

            float tx = r.x + S(14f);
            if (tracked)
            {
                float ds = Mathf.Round(S(7f));
                Tex(new Rect(tx, Mathf.Round(r.y + S(17f) - ds * 0.5f), ds, ds), _diamond, WithA(Gold, a));
                tx += ds + S(9f);
            }
            _serif.fontSize = Fs(16f);
            Text(new Rect(tx, r.y + S(7f), r.xMax - tx - S(8f), S(22f)), title, _serif,
                WithA(Ink, (done ? 0.45f : selected ? InkStrong : 0.80f) * a), 0f);
            _sans.fontSize = Fs(12f);
            Text(new Rect(r.x + S(14f), r.y + S(31f), r.width - S(22f), S(18f)), sub, _sans, WithA(Ink, InkFaint * a), 0f);
            return Click(r);
        }

        private float Label(float x, float y, string text, float a)
        {
            _sans.fontSize = Fs(12f);
            Text(new Rect(x, y, S(300f), S(18f)), text, _sans, WithA(Ink, InkFaint * a), 0f);
            return y + S(24f);
        }

        private float Paragraph(float x, float y, float w, string text, float size, float inkA, float a, GUIStyle style = null)
        {
            if (string.IsNullOrEmpty(text)) return y;
            var st = style ?? _sans;
            st.fontSize = Fs(size);
            st.wordWrap = true;
            float h = st.CalcHeight(Tmp(text), w);
            Text(new Rect(x, y, w, h), text, st, WithA(Ink, inkA * a), 0f);
            st.wordWrap = false;
            return y + h;
        }

        private void EmptyNote(Rect r, string text, float a)
        {
            _sans.fontSize = Fs(13f);
            Text(new Rect(r.x + S(14f), r.y + S(8f), r.width, S(20f)), text, _sans, WithA(Ink, InkFaint * a), 0f);
        }

        // ------------------------------------------------------------------ world helpers
        private Vector3 PlayerPos()
        {
            if (playerTransform != null) return playerTransform.position;
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        private float HeadingDeg()
        {
            var cam = Camera.main;
            if (cam != null) return cam.transform.eulerAngles.y;
            return playerTransform != null ? playerTransform.eulerAngles.y : 0f;
        }

        private float QuestDistance(StoreQuest q)
        {
            if (playerTransform == null && rpgStore != null && rpgStore.DistanceToActiveQuest < 999f) return rpgStore.DistanceToActiveQuest;
            Vector3 d = q.targetPosition - PlayerPos();
            return new Vector2(d.x, d.z).magnitude;
        }

        private string DistanceLabel(float metres)
        {
            int key = metres >= 1000f ? 100000 + Mathf.RoundToInt(metres / 100f) : Mathf.RoundToInt(metres);
            if (key == _distCacheKey) return _distCache;
            _distCacheKey = key;
            _distCache = metres >= 1000f ? (metres / 1000f).ToString("0.0") + " km" : key + " m";
            return _distCache;
        }

        // ------------------------------------------------------------------ drawing primitives
        private float S(float v) => v * _scale;
        private int Fs(float size) => Mathf.Max(9, Mathf.RoundToInt(size * _scale));
        private static float Ratio(float v, float max) => max > 0f ? Mathf.Clamp01(v / max) : 0f;
        private static Color WithA(Color c, float a) { c.a = Mathf.Clamp01(a); return c; }
        private static Rect Snap(Rect r) => new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));

        /// Brighter sky (higher sun) → firmer text shadow; dusk and night need very little.
        private float ShadowStrength() => Mathf.Lerp(0.45f, 0.85f, Mathf.InverseLerp(-6f, 55f, SunElevationDeg));

        private static void Fill(Rect r, Color c)
        {
            if (c.a <= 0.001f || Event.current.type != EventType.Repaint) return;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private static void Tex(Rect r, Texture tex, Color c)
        {
            if (tex == null || c.a <= 0.001f || Event.current.type != EventType.Repaint) return;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        private void Frame(Rect r, Color c)
        {
            r = Snap(r);
            Fill(new Rect(r.x, r.y, r.width, _px), c);
            Fill(new Rect(r.x, r.yMax - _px, r.width, _px), c);
            Fill(new Rect(r.x, r.y + _px, _px, r.height - 2f * _px), c);
            Fill(new Rect(r.xMax - _px, r.y + _px, _px, r.height - 2f * _px), c);
        }

        private void Text(Rect r, string s, GUIStyle st, Color c, float shadow)
        {
            if (string.IsNullOrEmpty(s) || c.a <= 0.001f || Event.current.type != EventType.Repaint) return;
            var content = Tmp(s);
            if (shadow > 0f)
            {
                float o = Mathf.Max(1f, Mathf.Round(_scale));
                st.normal.textColor = WithA(Shadow, c.a * shadow * 0.55f);
                st.Draw(new Rect(r.x, r.y + o, r.width, r.height), content, false, false, false, false);
                st.normal.textColor = WithA(Shadow, c.a * shadow);
                st.Draw(new Rect(r.x + o, r.y + o, r.width, r.height), content, false, false, false, false);
            }
            st.normal.textColor = c;
            st.Draw(r, content, false, false, false, false);
        }

        private float Measure(GUIStyle st, string s) => string.IsNullOrEmpty(s) ? 0f : Mathf.Ceil(st.CalcSize(Tmp(s)).x);
        private GUIContent Tmp(string s) { _gc.text = s; return _gc; }

        private static bool Hover(Rect r) => r.Contains(Event.current.mousePosition);

        private static bool Click(Rect r)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { e.Use(); return true; }
            return false;
        }

        private static string SentenceCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }

        // ------------------------------------------------------------------ setup
        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            HashSet<string> installed = null;
            try { installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase); }
            catch (Exception) { /* platform without OS font enumeration */ }

            Font sans = PickFont(installed, new[] { "Inter", "Segoe UI", "Helvetica Neue", "SF Pro Text", "Noto Sans", "DejaVu Sans", "Arial" });
            Font serif = PickFont(installed, new[] { "EB Garamond", "Cormorant Garamond", "Iowan Old Style", "Palatino Linotype", "Georgia", "Noto Serif", "DejaVu Serif" });

            _sans = MakeStyle(sans ?? GUI.skin.font);
            _serif = MakeStyle(serif ?? sans ?? GUI.skin.font);
        }

        private static Font PickFont(HashSet<string> installed, string[] prefs)
        {
            if (installed == null) return null;
            foreach (var name in prefs)
                if (installed.Contains(name)) return Font.CreateDynamicFontFromOSFont(name, 16);
            return null;
        }

        private static GUIStyle MakeStyle(Font font)
        {
            return new GUIStyle
            {
                font = font,
                fontSize = 13,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                normal = { textColor = Ink }
            };
        }

        /// 256×1 alpha ramp: transparent ends, solid middle — the compass hairline.
        private static Texture2D BuildFadeTexture()
        {
            const int w = 256;
            var t = new Texture2D(w, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[w];
            for (int i = 0; i < w; i++)
            {
                float u = Mathf.Abs(i / (w - 1f) * 2f - 1f);
                float aa = 1f - Mathf.SmoothStep(0.45f, 1f, u);
                px[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(aa * 255f));
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// 64×64 anti-aliased 45° diamond (white; tinted gold at draw time).
        private static Texture2D BuildDiamondTexture()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f, r = n * 0.5f - 1.5f;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Abs(x - c) + Mathf.Abs(y - c);
                float aa = Mathf.Clamp01(r - d + 0.5f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(aa * 255f));
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }
    }
}
