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
    /// Menus (same file, same primitives — no stock IMGUI chrome):
    ///   Title          on Play: live world behind a left gradient scrim, camera orbits Oakhaven
    ///                  Continue · New journey (inline confirm) · Settings · Quit
    ///   Pause (Esc)    Time.timeScale = 0, HUD fades to a trace
    ///                  Resume · Journal · Settings · Save journey · Return to title · Quit
    ///   Settings       Display · Controls · Audio · Save tabs, hairline sliders and text toggles,
    ///                  persisted to worldmill_settings.json (WorldMillGameSettings)
    ///   Esc hierarchy  confirm → settings → journal → NPC dialogue → pause ⇄ resume
    ///
    /// Gold (#d4a853) marks where to look or what is chosen: the quest diamond, the dimmed XP
    /// hairline, the focused menu item, active tab underlines, chosen toggle options and slider
    /// fills / thumbs. Everything else is ink on dark glass.
    /// Pure IMGUI: no assets, no UI packages. All UI timing runs on unscaled time.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldMillMinimalHud : MonoBehaviour
    {
        [Header("References (auto-resolved when empty)")]
        public WorldMillLocalRpgStore rpgStore;
        public WorldMillPlayerController playerController;
        public Transform playerTransform;

        [Header("Menus")]
        [Tooltip("Show the title screen when Play starts (live world + scenic camera orbit behind it).")]
        public bool showTitleOnStart = true;

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

        /// Title screen, or Settings opened from it. The player controller orbits the camera meanwhile.
        public bool IsTitleOpen => _menu == Menu.Title || (_menu == Menu.Settings && _settingsFrom == Menu.Title);
        /// Pause menu, or Settings opened from it (Time.timeScale = 0).
        public bool IsPaused => _menu == Menu.Pause || (_menu == Menu.Settings && _settingsFrom == Menu.Pause);
        public bool IsSettingsOpen => _menu == Menu.Settings;
        public bool IsMenuOpen => _menu != Menu.None;
        /// True whenever gameplay input (movement, combat, hotkeys, E) must be ignored.
        /// Also true for the frame a menu closes, so the Space / Enter that chose "Resume" is not a jump.
        public bool IsInputBlocked => IsCodexOpen || _menu != Menu.None || Time.frameCount <= _blockedThroughFrame;
        public WorldMillGameSettings Settings => _settings;

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
        private static readonly Color Scrim    = new Color(0.024f, 0.027f, 0.035f, 1f); // #06070a

        private const float InkStrong = 0.94f, InkMuted = 0.72f, InkFaint = 0.48f;
        private const float HairlineA = 0.16f, HairlineStrongA = 0.32f;

        // ui-tokens.json → motion.*
        private const float TrailDelay = 0.45f;   // s before the drain trail starts to fall
        private const float TrailMinRate = 0.18f; // fraction / s
        private const float HealRate = 0.9f;      // fraction / s
        private const float ToastIn = 0.25f, ToastHold = 3.2f, ToastOut = 0.8f;
        private const float FadeFast = 0.18f, FadeSheet = 0.22f, FadeHud = 0.25f, FadeMenu = 0.45f;

        // ------------------------------------------------------------------ state
        private bool _hudVisible = true;
        private float _hudAlpha = 1f, _codexAlpha, _promptAlpha;

        private float _hpFill = 1f, _hpTrail = 1f, _hpHold, _hpPrev = 1f;
        private float _stFill = 1f, _stTrail = 1f, _stHold, _stPrev = 1f;
        private float _xpFill;
        private int _xpLevelSeen = -1;

        private float _scale = 1f, _px = 1f;
        private Texture2D _fadeH, _diamond, _scrimL;
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

        // Menus
        private enum Menu { None, Title, Pause, Settings }
        private enum MenuAction { Continue, NewJourney, Settings, Quit, Resume, Journal, SaveJourney, ReturnToTitle }
        private enum SettingsTab { Display, Controls, Audio, Save }
        private enum Setting
        {
            WindowMode, VSync, Fov, Sun, Clouds, HudScale, ShowHud,
            MouseSensitivity, InvertY, CameraDistance,
            MasterVolume, Mute,
            SaveNow, ResetJourney, RestoreDefaults
        }

        private Menu _menu = Menu.None;
        private Menu _settingsFrom = Menu.Title;
        private SettingsTab _setTab = SettingsTab.Display;
        private readonly List<MenuAction> _menuItems = new();
        private int _menuFocus, _setFocus, _confirmChoice = 1;
        private bool _confirmNewJourney, _confirmResetJourney;
        private float _titleAlpha, _pauseAlpha, _settingsAlpha, _scrimAlpha, _hudMenuMul = 1f;
        private int _dragSlider = -1;
        private Vector3 _lastMouse;
        private bool _mouseMoved, _pausedTime;
        private float _nextResolve;
        private int _blockedThroughFrame = -1;

        private WorldMillGameSettings _settings, _defaults;
        private bool _settingsDirty, _appliedToPlayer;
        private float _settingsDirtyAt;

        private string _localToast;
        private float _localToastAt = -10f;

        // ------------------------------------------------------------------ lifecycle
        private void Awake()
        {
            _fadeH = BuildFadeTexture();
            _diamond = BuildDiamondTexture();
            _scrimL = BuildScrimTexture();
            float hp01 = Ratio(CurrentHp, MaxHp), st01 = Ratio(CurrentStamina, MaxStamina);
            _hpFill = _hpTrail = _hpPrev = hp01;
            _stFill = _stTrail = _stPrev = st01;
        }

        private void Start()
        {
            ResolveRefs();
            _defaults = CaptureSceneDefaults();
            _settings = WorldMillGameSettings.LoadOr(_defaults);
            ApplyAllSettings();
            if (showTitleOnStart) OpenTitle();
            else _hudMenuMul = 1f;
        }

        private void OnDisable()
        {
            SetTimePaused(false);
            RestoreCursor();
            FlushSettings();
        }

        private void OnDestroy()
        {
            SetTimePaused(false);
            if (_fadeH != null) Destroy(_fadeH);
            if (_diamond != null) Destroy(_diamond);
            if (_scrimL != null) Destroy(_scrimL);
        }

        private void ResolveRefs()
        {
            if (rpgStore == null) rpgStore = FindFirstObjectByType<WorldMillLocalRpgStore>();
            if (playerController == null) playerController = FindFirstObjectByType<WorldMillPlayerController>();
            if (playerTransform == null)
            {
                if (playerController != null) playerTransform = playerController.transform;
                else if (rpgStore != null) playerTransform = rpgStore.playerTransform;
            }
        }

        private Camera PlayerCamera =>
            playerController != null && playerController.playerCamera != null ? playerController.playerCamera : Camera.main;

        private void Update()
        {
            if ((rpgStore == null || playerController == null || playerTransform == null) && Time.unscaledTime >= _nextResolve)
            {
                _nextResolve = Time.unscaledTime + 1f;
                ResolveRefs();
            }
            if (_settings != null && !_appliedToPlayer && playerController != null) ApplyPlayerSettings();

            Vector3 mouse = Input.mousePosition;
            _mouseMoved = (mouse - _lastMouse).sqrMagnitude > 0.25f;
            _lastMouse = mouse;

            bool wasBlocked = IsCodexOpen || _menu != Menu.None;
            HandleKeys();
            if (wasBlocked) _blockedThroughFrame = Time.frameCount;

            float dt = Time.unscaledDeltaTime;   // UI always runs on unscaled time (pause sets timeScale = 0)
            _hudAlpha = Mathf.MoveTowards(_hudAlpha, _hudVisible ? 1f : 0f, dt / FadeHud);
            _codexAlpha = Mathf.MoveTowards(_codexAlpha, IsCodexOpen ? 1f : 0f, dt / FadeSheet);
            bool promptWanted = rpgStore != null && rpgStore.CanCompleteActiveQuest && rpgStore.ActiveQuest != null && !IsInputBlocked;
            _promptAlpha = Mathf.MoveTowards(_promptAlpha, promptWanted ? 1f : 0f, dt / FadeFast);

            // Menus: title hides the HUD entirely, pause leaves a faint trace of it.
            float hudTarget = IsTitleOpen ? 0f : IsPaused ? 0.15f : 1f;
            _hudMenuMul = Mathf.MoveTowards(_hudMenuMul, hudTarget, dt / (IsTitleOpen ? FadeSheet : FadeMenu));
            bool menuLayer = _menu != Menu.None && !IsCodexOpen;
            _scrimAlpha = Mathf.MoveTowards(_scrimAlpha, menuLayer ? 1f : 0f, dt / FadeMenu);
            _titleAlpha = Mathf.MoveTowards(_titleAlpha, _menu == Menu.Title ? 1f : 0f, dt / FadeMenu);
            _pauseAlpha = Mathf.MoveTowards(_pauseAlpha, _menu == Menu.Pause && !IsCodexOpen ? 1f : 0f, dt / FadeSheet);
            _settingsAlpha = Mathf.MoveTowards(_settingsAlpha, _menu == Menu.Settings ? 1f : 0f, dt / FadeSheet);

            StepBar(ref _hpFill, ref _hpTrail, ref _hpHold, ref _hpPrev, Ratio(CurrentHp, MaxHp), dt);
            StepBar(ref _stFill, ref _stTrail, ref _stHold, ref _stPrev, Ratio(CurrentStamina, MaxStamina), dt);

            var save = rpgStore != null ? rpgStore.Save : null;
            if (save != null)
            {
                float xp01 = save.xpToNext > 0 ? Mathf.Clamp01(save.xp / (float)save.xpToNext) : 0f;
                if (_xpLevelSeen != save.level) { _xpLevelSeen = save.level; _xpFill = Mathf.Min(_xpFill, xp01); }
                _xpFill = Mathf.MoveTowards(_xpFill, xp01, dt * 0.6f);
            }

            if (_settingsDirty && _dragSlider < 0 && Time.unscaledTime - _settingsDirtyAt > 0.35f) FlushSettings();
        }

        /// Esc hierarchy: confirm → settings → journal → pause/resume. Then menu keyboard navigation,
        /// then the gameplay HUD keys (J, H) which only work while playing or paused.
        private void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { HandleEscape(); return; }

            if (_menu != Menu.None && !IsCodexOpen)
            {
                if (_menu == Menu.Settings) HandleSettingsKeys();
                else HandleMenuListKeys();
            }

            if (_menu == Menu.None && Input.GetKeyDown(hideHudKey))
            {
                _hudVisible = !_hudVisible;
                if (!_hudVisible) SetCodex(false);
                if (_settings != null) { _settings.showHud = _hudVisible; MarkSettingsDirty(); }
            }
            if ((_menu == Menu.None && _hudVisible) || _menu == Menu.Pause)
            {
                if (Input.GetKeyDown(journalKey)) SetCodex(!IsCodexOpen);
            }
        }

        private void HandleEscape()
        {
            if (_menu == Menu.Settings)
            {
                if (_confirmResetJourney) { _confirmResetJourney = false; return; }
                CloseSettings();
                return;
            }
            if (IsCodexOpen) { SetCodex(false); return; }
            if (_menu == Menu.Title) { _confirmNewJourney = false; return; }
            if (_menu == Menu.Pause) { Resume(); return; }

            // Playing: an open NPC dialogue takes Esc first (either script may run first this frame).
            if (WorldMillNpc.OpenDialogueCount > 0 || WorldMillNpc.EscapeConsumedFrame == Time.frameCount) return;
            OpenPause();
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
                var aq = rpgStore != null ? rpgStore.ActiveQuest : null;
                if (aq != null) _selQuestId = aq.id;
            }
            RefreshCursor();
        }

        /// Any overlay (journal, title, pause, settings) frees the cursor; it is restored after.
        private void RefreshCursor()
        {
            if (IsCodexOpen || _menu != Menu.None)
            {
                if (!_savedCursor) { _prevLock = Cursor.lockState; _prevVisible = Cursor.visible; _savedCursor = true; }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
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
            bool menus = _scrimAlpha > 0.001f || _titleAlpha > 0.001f || _pauseAlpha > 0.001f || _settingsAlpha > 0.001f;
            if (_hudAlpha <= 0.001f && _codexAlpha <= 0.001f && !menus && !ToastActive()) return;
            EnsureStyles();
            if (Event.current.rawType == EventType.MouseUp) _dragSlider = -1;

            float baseScale = Mathf.Max(0.6f, Screen.height / 1080f);
            float hudScale = _settings != null ? _settings.hudScale : 1f;
            GUI.depth = -20;

            // HUD layer, at the player's HUD scale
            SetScale(baseScale * hudScale);
            float a = _hudAlpha * _hudMenuMul;
            float hudUnderSheet = a * (1f - 0.85f * _codexAlpha);
            DrawCompass(hudUnderSheet);
            DrawTracker(hudUnderSheet);
            DrawVitals(hudUnderSheet);
            DrawPrompt(a * _promptAlpha);

            // Sheets and menus, at the base 1080p scale so dragging HUD scale never moves the slider
            SetScale(baseScale);
            if (_codexAlpha > 0.001f) DrawJournal(_codexAlpha);
            if (_scrimAlpha > 0.001f) DrawMenuScrim(_scrimAlpha);
            if (_titleAlpha > 0.001f) DrawTitle(_titleAlpha);
            if (_pauseAlpha > 0.001f) DrawPause(_pauseAlpha);
            if (_settingsAlpha > 0.001f) DrawSettings(_settingsAlpha);

            // Toasts stay readable over menus (e.g. "Journey saved", "Settings restored")
            SetScale(baseScale * hudScale);
            DrawToast(Mathf.Max(a, menus ? 1f : 0f));
            SetScale(baseScale);
        }

        private void SetScale(float s)
        {
            _scale = s;
            _px = Mathf.Max(1f, Mathf.Round(_scale));
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
        private bool CurrentToast(out string msg, out float age)
        {
            msg = rpgStore != null ? rpgStore.ToastMessage : _localToast;
            float at = rpgStore != null ? rpgStore.ToastTimestamp : _localToastAt;
            if (rpgStore != null && _localToastAt > at) { msg = _localToast; at = _localToastAt; }
            age = Time.unscaledTime - at;
            return !string.IsNullOrEmpty(msg) && age >= 0f && age <= ToastIn + ToastHold + ToastOut;
        }

        private bool ToastActive() => CurrentToast(out _, out _);

        private void DrawToast(float a)
        {
            if (a <= 0.001f || !CurrentToast(out string msg, out float age)) return;
            float env = age < ToastIn ? Mathf.SmoothStep(0f, 1f, age / ToastIn)
                      : age < ToastIn + ToastHold ? 1f
                      : 1f - Mathf.SmoothStep(0f, 1f, (age - ToastIn - ToastHold) / ToastOut);
            float ta = env * a;
            float rise = (1f - Mathf.Clamp01(age / ToastIn)) * S(4f);

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
                if (on) Fill(new Rect(tr.x, Mathf.Round(tr.yMax + S(2f)), tw, _px), WithA(Gold, a));
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
            Text(new Rect(innerX, footY, S(300f), S(18f)), IsPaused ? "J or Esc to return" : "J or Esc to close", _sans, WithA(Ink, InkFaint * a), 0f);
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

        // ------------------------------------------------------------------ menus: state machine
        private void OpenTitle()
        {
            SetCodex(false);
            SetTimePaused(false);
            _menu = Menu.Title;
            _menuFocus = 0;
            _confirmNewJourney = false;
            _hudMenuMul = 0f;
            RefreshCursor();
        }

        private void CloseTitle()
        {
            _menu = Menu.None;
            _confirmNewJourney = false;
            RefreshCursor();
        }

        private void OpenPause()
        {
            _menu = Menu.Pause;
            _menuFocus = 0;
            SetTimePaused(true);
            RefreshCursor();
        }

        private void Resume()
        {
            SetCodex(false);
            _menu = Menu.None;
            SetTimePaused(false);
            RefreshCursor();
        }

        private void ReturnToTitle()
        {
            if (rpgStore != null) rpgStore.SaveNow(false);
            FlushSettings();
            OpenTitle();
        }

        private void OpenSettings()
        {
            if (_menu != Menu.Title && _menu != Menu.Pause) return;
            _settingsFrom = _menu;
            _menu = Menu.Settings;
            _setFocus = 0;
            _confirmResetJourney = false;
            SyncLiveValuesIntoSettings();
            RefreshCursor();
        }

        private void CloseSettings()
        {
            FlushSettings();
            _confirmResetJourney = false;
            _menu = _settingsFrom;
            RefreshCursor();
        }

        private void StartNewJourney()
        {
            _confirmNewJourney = false;
            if (rpgStore != null) rpgStore.ResetSave();
            if (playerController != null) playerController.ResetToSpawn();
            CloseTitle();
        }

        private void QuitGame()
        {
            if (rpgStore != null) rpgStore.SaveNow(false);
            FlushSettings();
            SetTimePaused(false);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void SetTimePaused(bool paused)
        {
            if (paused)
            {
                Time.timeScale = 0f;
                _pausedTime = true;
            }
            else if (_pausedTime)
            {
                Time.timeScale = 1f;
                _pausedTime = false;
            }
        }

        private void ShowToast(string msg)
        {
            if (rpgStore != null) rpgStore.ShowToast(msg);
            else { _localToast = msg; _localToastAt = Time.unscaledTime; }
        }

        private void BuildMenuItems()
        {
            _menuItems.Clear();
            if (_menu == Menu.Title)
            {
                if (rpgStore != null && rpgStore.HasSaveProgress) _menuItems.Add(MenuAction.Continue);
                _menuItems.Add(MenuAction.NewJourney);
                _menuItems.Add(MenuAction.Settings);
                _menuItems.Add(MenuAction.Quit);
            }
            else if (_menu == Menu.Pause)
            {
                _menuItems.Add(MenuAction.Resume);
                _menuItems.Add(MenuAction.Journal);
                _menuItems.Add(MenuAction.Settings);
                _menuItems.Add(MenuAction.SaveJourney);
                _menuItems.Add(MenuAction.ReturnToTitle);
                _menuItems.Add(MenuAction.Quit);
            }
            _menuFocus = _menuItems.Count == 0 ? 0 : Mathf.Clamp(_menuFocus, 0, _menuItems.Count - 1);
        }

        private static string MenuLabel(MenuAction m) => m switch
        {
            MenuAction.Continue => "Continue",
            MenuAction.NewJourney => "New journey",
            MenuAction.Settings => "Settings",
            MenuAction.Quit => "Quit",
            MenuAction.Resume => "Resume",
            MenuAction.Journal => "Journal",
            MenuAction.SaveJourney => "Save journey",
            MenuAction.ReturnToTitle => "Return to title",
            _ => ""
        };

        private void Activate(MenuAction m)
        {
            switch (m)
            {
                case MenuAction.Continue: CloseTitle(); break;
                case MenuAction.NewJourney:
                    if (rpgStore != null && rpgStore.HasSaveProgress && !_confirmNewJourney)
                    {
                        _confirmNewJourney = true;
                        _confirmChoice = 1;   // Cancel is the safe default
                    }
                    else StartNewJourney();
                    break;
                case MenuAction.Settings: OpenSettings(); break;
                case MenuAction.Quit: QuitGame(); break;
                case MenuAction.Resume: Resume(); break;
                case MenuAction.Journal: SetCodex(true); break;
                case MenuAction.SaveJourney:
                    if (rpgStore != null) rpgStore.SaveNow();
                    else ShowToast("Nothing to save yet");
                    FlushSettings();
                    break;
                case MenuAction.ReturnToTitle: ReturnToTitle(); break;
            }
        }

        private void HandleMenuListKeys()
        {
            BuildMenuItems();
            if (_menuItems.Count == 0) return;
            bool confirming = _confirmNewJourney && _menuItems[_menuFocus] == MenuAction.NewJourney;
            if (confirming)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _confirmChoice = 0;
                if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _confirmChoice = 1;
                if (SubmitPressed())
                {
                    if (_confirmChoice == 0) StartNewJourney();
                    else _confirmNewJourney = false;
                    return;
                }
            }
            int step = (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S) ? 1 : 0)
                     - (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W) ? 1 : 0);
            if (step != 0)
            {
                _menuFocus = (_menuFocus + step + _menuItems.Count) % _menuItems.Count;
                _confirmNewJourney = false;
            }
            else if (!confirming && SubmitPressed()) Activate(_menuItems[_menuFocus]);
        }

        private static bool SubmitPressed() =>
            Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space);

        // ------------------------------------------------------------------ menus: drawing
        /// Left-side dark gradient so text reads over the live world; never an opaque box.
        private void DrawMenuScrim(float a)
        {
            if (IsPaused) Fill(new Rect(0f, 0f, Screen.width, Screen.height), WithA(Scrim, 0.22f * a));
            float w = Mathf.Round(Mathf.Max(S(900f), Screen.width * 0.58f));
            Tex(new Rect(0f, 0f, w, Screen.height), _scrimL, WithA(Scrim, 0.82f * a));
        }

        private void DrawTitle(float a)
        {
            BuildMenuItems();
            float x = Mathf.Round(S(120f));
            float lift = (1f - a) * S(10f);
            float listH = _menuItems.Count * S(46f) + (_confirmNewJourney ? S(22f) : 0f);
            float y = Mathf.Round(Screen.height * 0.5f - (S(170f) + listH) * 0.5f + lift);
            float sh = MenuShadow();

            _serif.fontSize = Fs(64f);
            Text(new Rect(x - S(3f), y, S(640f), S(84f)), "WorldMill", _serif, WithA(Ink, InkStrong * a), sh);
            y += S(84f);
            _sans.fontSize = Fs(15f);
            Text(new Rect(x, y, S(480f), S(22f)), "Oakhaven Crossing", _sans, WithA(Ink, InkMuted * a), sh);
            y += S(46f);
            Fill(new Rect(x, Mathf.Round(y), Mathf.Round(S(48f)), _px), WithA(Ink, HairlineStrongA * a));
            y += S(36f);

            DrawMenuList(x, y, a);

            _sans.fontSize = Fs(12f);
            Text(new Rect(x, Mathf.Round(Screen.height - S(64f)), S(600f), S(18f)),
                "Arrow keys to choose  ·  Enter to confirm", _sans, WithA(Ink, InkFaint * a), sh);
        }

        private void DrawPause(float a)
        {
            BuildMenuItems();
            float x = Mathf.Round(S(120f));
            float lift = (1f - a) * S(8f);
            float listH = _menuItems.Count * S(46f);
            float y = Mathf.Round(Screen.height * 0.5f - (S(118f) + listH) * 0.5f + lift);
            float sh = MenuShadow();

            _serif.fontSize = Fs(34f);
            Text(new Rect(x - S(1f), y, S(480f), S(46f)), "Paused", _serif, WithA(Ink, InkStrong * a), sh);
            y += S(50f);
            _sans.fontSize = Fs(13f);
            Text(new Rect(x, y, S(560f), S(20f)), JourneyMeta(), _sans, WithA(Ink, InkFaint * a), sh);
            y += S(34f);
            Fill(new Rect(x, Mathf.Round(y), Mathf.Round(S(48f)), _px), WithA(Ink, HairlineStrongA * a));
            y += S(34f);

            DrawMenuList(x, y, a);

            _sans.fontSize = Fs(12f);
            Text(new Rect(x, Mathf.Round(Screen.height - S(64f)), S(600f), S(18f)),
                "Esc to resume  ·  The world waits", _sans, WithA(Ink, InkFaint * a), sh);
        }

        /// "Lv. 2  ·  Watch of the High Stones" — quiet metadata for Continue and Paused.
        private string JourneyMeta()
        {
            var save = rpgStore != null ? rpgStore.Save : null;
            if (save == null) return "";
            var q = rpgStore.ActiveQuest;
            return "Lv. " + save.level + "  ·  " + (q != null && !q.completed ? q.title : "Every quest complete");
        }

        /// Vertical editorial text menu. Focus = ink strong + a short gold rule in the margin.
        private void DrawMenuList(float x, float y, float a)
        {
            float rowH = S(46f), sh = MenuShadow();
            for (int i = 0; i < _menuItems.Count; i++)
            {
                var item = _menuItems[i];
                var row = new Rect(x - S(40f), y, S(560f), rowH);
                if (_mouseMoved && Hover(row) && _menuFocus != i) { _menuFocus = i; if (item != MenuAction.NewJourney) _confirmNewJourney = false; }
                bool focus = _menuFocus == i;
                float midY = y + rowH * 0.5f;

                if (focus) Fill(new Rect(Mathf.Round(x - S(32f)), Mathf.Round(midY), Mathf.Round(S(18f)), _px), WithA(Gold, a));

                bool confirming = item == MenuAction.NewJourney && _confirmNewJourney;
                _serif.fontSize = Fs(23f);
                string label = confirming ? "Start fresh?" : MenuLabel(item);
                float lw = Measure(_serif, label);
                TextMid(x, midY, label, _serif, WithA(Ink, (focus ? InkStrong : InkMuted) * a), sh);

                if (confirming)
                {
                    int clicked = ConfirmChoices(x + lw + S(28f), midY, a, ref _confirmChoice);
                    _sans.fontSize = Fs(12f);
                    Text(new Rect(x, Mathf.Round(y + rowH - S(6f)), S(520f), S(18f)),
                        "Your current journey and campfires will be lost.", _sans, WithA(Ink, InkFaint * a), sh);
                    if (clicked == 0) StartNewJourney();
                    else if (clicked == 1) _confirmNewJourney = false;
                    y += rowH + S(22f);
                    continue;
                }

                if (item == MenuAction.Continue)
                {
                    _sans.fontSize = Fs(12f);
                    TextMid(x + lw + S(18f), midY + S(2f), JourneyMeta(), _sans, WithA(Ink, InkFaint * a), sh);
                }

                if (Click(row)) { _menuFocus = i; Activate(item); }
                y += rowH;
            }
        }

        /// Inline "Confirm   Cancel". Focused choice carries the gold underline. Returns the clicked index or -1.
        private int ConfirmChoices(float x, float midY, float a, ref int choice)
        {
            int clicked = -1;
            _sans.fontSize = Fs(14f);
            string[] opts = { "Confirm", "Cancel" };
            for (int i = 0; i < 2; i++)
            {
                float w = Measure(_sans, opts[i]);
                var r = new Rect(x - S(6f), midY - S(14f), w + S(12f), S(28f));
                bool hov = Hover(r);
                if (_mouseMoved && hov) choice = i;
                bool on = choice == i;
                TextMid(x, midY, opts[i], _sans, WithA(Ink, (on ? InkStrong : hov ? 0.78f : InkFaint) * a), 0f);
                if (on) Fill(new Rect(Mathf.Round(x), Mathf.Round(midY + S(11f)), Mathf.Round(w), _px), WithA(Gold, a));
                if (Click(r)) { choice = i; clicked = i; }
                x += w + S(24f);
            }
            return clicked;
        }

        private float ConfirmWidth()
        {
            _sans.fontSize = Fs(14f);
            return Measure(_sans, "Confirm") + S(24f) + Measure(_sans, "Cancel");
        }

        private float MenuShadow() => ShadowStrength() * 0.55f;

        // ------------------------------------------------------------------ settings sheet
        private void DrawSettings(float a)
        {
            float w = Mathf.Round(Mathf.Min(S(720f), Screen.width - S(168f)));
            float h = Mathf.Round(Mathf.Min(S(690f), Screen.height - S(96f)));
            float lift = (1f - a) * S(10f);
            var sheet = new Rect(Mathf.Round(S(84f)), Mathf.Round((Screen.height - h) * 0.5f + lift), w, h);
            Fill(sheet, WithA(Surface, 0.88f * a));
            Frame(sheet, WithA(Ink, 0.14f * a));

            float pad = S(36f);
            float innerX = sheet.x + pad, innerW = sheet.width - pad * 2f;
            float top = sheet.y + S(28f);

            _serif.fontSize = Fs(26f);
            Text(new Rect(innerX, top, S(300f), S(36f)), "Settings", _serif, WithA(Ink, InkStrong * a), 0f);

            // Tabs, right-aligned. Active tab: ink strong + gold underline.
            float tabRight = innerX + innerW;
            _sans.fontSize = Fs(13f);
            for (int i = SettingsTabNames.Length - 1; i >= 0; i--)
            {
                string tl = SettingsTabNames[i];
                float tw = Measure(_sans, tl);
                var tr = new Rect(tabRight - tw, top + S(10f), tw, S(22f));
                var hit = new Rect(tr.x - S(8f), tr.y - S(6f), tw + S(16f), S(34f));
                bool on = (int)_setTab == i, hov = Hover(hit);
                Text(tr, tl, _sans, WithA(Ink, (on ? InkStrong : hov ? 0.78f : InkFaint) * a), 0f);
                if (on) Fill(new Rect(tr.x, Mathf.Round(tr.yMax + S(2f)), tw, _px), WithA(Gold, a));
                if (Click(hit)) SelectSettingsTab((SettingsTab)i);
                tabRight -= tw + S(28f);
            }

            float ruleY = Mathf.Round(top + S(52f));
            Fill(new Rect(innerX, ruleY, innerW, _px), WithA(Ink, 0.10f * a));

            float footY = sheet.yMax - S(42f);
            var body = new Rect(innerX, ruleY + S(8f), innerW, footY - ruleY - S(20f));

            var rows = TabRows[(int)_setTab];
            float rowH = S(58f), y = body.y;
            for (int i = 0; i < rows.Length && y + rowH <= body.yMax + 1f; i++)
            {
                DrawSettingRow(new Rect(body.x, y, body.width, rowH), rows[i], i, a);
                y += rowH;
            }

            if (_setTab == SettingsTab.Controls) DrawKeybinds(new Rect(body.x, y + S(22f), body.width, body.yMax - y - S(22f)), a);
            if (_setTab == SettingsTab.Save)
            {
                _sans.fontSize = Fs(12f);
                Paragraph(body.x, y + S(18f), body.width,
                    "Settings save automatically to " + WorldMillGameSettings.FileName + ". Your journey saves on every quest and campfire.",
                    12f, InkFaint, a);
            }

            // Footer
            _sans.fontSize = Fs(12f);
            Text(new Rect(innerX, footY, S(420f), S(18f)), "Tab to switch  ·  Esc to go back", _sans, WithA(Ink, InkFaint * a), 0f);
            _sans.fontSize = Fs(13f);
            string back = _settingsFrom == Menu.Title ? "Back to title" : "Back";
            float bw = Measure(_sans, back);
            var br = new Rect(innerX + innerW - bw, footY - S(2f), bw, S(20f));
            var bhit = new Rect(br.x - S(8f), br.y - S(6f), bw + S(16f), S(32f));
            bool bh = Hover(bhit);
            Text(br, back, _sans, WithA(Ink, (bh ? InkStrong : InkMuted) * a), 0f);
            if (bh) Fill(new Rect(br.x, Mathf.Round(br.yMax + S(1f)), bw, _px), WithA(Ink, HairlineStrongA * a));
            if (Click(bhit)) CloseSettings();
        }

        private void SelectSettingsTab(SettingsTab t)
        {
            if (_setTab == t) return;
            _setTab = t;
            _setFocus = 0;
            _confirmResetJourney = false;
        }

        private void DrawSettingRow(Rect r, Setting s, int index, float a)
        {
            r = Snap(r);
            if (_mouseMoved && Hover(r) && _setFocus != index) { _setFocus = index; if (s != Setting.ResetJourney) _confirmResetJourney = false; }
            bool focus = _setFocus == index;
            if (focus)
            {
                Fill(new Rect(r.x - S(14f), r.y + _px, r.width + S(28f), r.height - _px), WithA(Ink, 0.035f * a));
                Fill(new Rect(r.x - S(14f), r.y + S(12f), _px, r.height - S(24f)), WithA(Gold, a));
            }
            Fill(new Rect(r.x, r.yMax - _px, r.width, _px), WithA(Ink, 0.07f * a));

            // Label + hint
            _sans.fontSize = Fs(14f);
            Text(new Rect(r.x, r.y + S(10f), r.width * 0.5f, S(20f)), SettingLabel(s), _sans, WithA(Ink, (focus ? InkStrong : 0.84f) * a), 0f);
            string hint = s == Setting.ResetJourney && _confirmResetJourney ? "Removes campfires, quests and levels." : SettingHint(s);
            _sans.fontSize = Fs(12f);
            Text(new Rect(r.x, r.y + S(31f), r.width * 0.55f, S(18f)), hint, _sans, WithA(Ink, InkFaint * a), 0f);

            float right = r.xMax, midY = r.y + r.height * 0.5f;
            if (IsSlider(s)) DrawSliderControl(s, right, midY, a, focus);
            else if (IsAction(s)) DrawActionControl(s, right, midY, a, focus);
            else DrawChoiceControl(s, right, midY, a);
        }

        private void DrawChoiceControl(Setting s, float right, float midY, float a)
        {
            ChoiceLabels(s, out string o0, out string o1);
            _sans.fontSize = Fs(13f);
            int cur = GetChoice(s);
            float w1 = Measure(_sans, o1), w0 = Measure(_sans, o0), gap = S(22f);
            float x1 = Mathf.Round(right - w1), x0 = Mathf.Round(x1 - gap - w0);
            for (int i = 0; i < 2; i++)
            {
                string label = i == 0 ? o0 : o1;
                float x = i == 0 ? x0 : x1, w = i == 0 ? w0 : w1;
                var hit = new Rect(x - S(8f), midY - S(14f), w + S(16f), S(28f));
                bool on = cur == i, hov = Hover(hit);
                TextMid(x, midY, label, _sans, WithA(Ink, (on ? InkStrong : hov ? 0.78f : InkFaint) * a), 0f);
                if (on) Fill(new Rect(x, Mathf.Round(midY + S(11f)), Mathf.Round(w), _px), WithA(Gold, a));
                if (Click(hit) && !on) SetChoice(s, i);
            }
        }

        /// Hairline slider: 1px ink track, gold active fill, thin gold thumb, tabular value on the right.
        private void DrawSliderControl(Setting s, float right, float midY, float a, bool focus)
        {
            SliderRange(s, out float min, out float max, out _, out _);
            float v = GetSlider(s);
            float valueW = S(64f), trackW = Mathf.Round(S(230f));
            float x0 = Mathf.Round(right - valueW - S(18f) - trackW);
            float cy = Mathf.Round(midY);
            int id = (int)s;

            var hit = new Rect(x0 - S(10f), cy - S(14f), trackW + S(20f), S(28f));
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
            {
                _dragSlider = id;
                SetSliderFromMouse(s, e.mousePosition.x, x0, trackW, min, max);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _dragSlider == id)
            {
                SetSliderFromMouse(s, e.mousePosition.x, x0, trackW, min, max);
                e.Use();
            }

            float t = Mathf.InverseLerp(min, max, GetSlider(s));
            bool active = _dragSlider == id || Hover(hit) || focus;
            float fillH = Mathf.Max(_px, Mathf.Round(S(2f)));
            Fill(new Rect(x0, cy, trackW, _px), WithA(Ink, (active ? 0.30f : 0.22f) * a));
            Fill(new Rect(x0, Mathf.Round(cy - (fillH - _px) * 0.5f), Mathf.Round(trackW * t), fillH), WithA(Gold, 0.85f * a));
            float thumbW = Mathf.Max(2f, Mathf.Round(S(2f))), thumbH = Mathf.Round(S(active ? 16f : 13f));
            Fill(new Rect(Mathf.Round(x0 + trackW * t - thumbW * 0.5f), Mathf.Round(cy - thumbH * 0.5f), thumbW, thumbH), WithA(Gold, a));

            _sans.fontSize = Fs(13f);
            string txt = SliderText(s, v);
            float tw = Measure(_sans, txt);
            TextMid(right - tw, midY, txt, _sans, WithA(Ink, (active ? InkStrong : InkMuted) * a), 0f);
        }

        private void SetSliderFromMouse(Setting s, float mouseX, float x0, float w, float min, float max)
        {
            float t = Mathf.Clamp01((mouseX - x0) / Mathf.Max(1f, w));
            SetSlider(s, Mathf.Lerp(min, max, t));
        }

        /// Quiet framed button (same as the journal's Track), or the inline reset confirmation.
        private void DrawActionControl(Setting s, float right, float midY, float a, bool focus)
        {
            if (s == Setting.ResetJourney && _confirmResetJourney)
            {
                float cw = ConfirmWidth();
                _sans.fontSize = Fs(14f);
                string q = "Start fresh?";
                float qw = Measure(_sans, q);
                float cx = right - cw;
                TextMid(cx - S(20f) - qw, midY, q, _sans, WithA(Ink, InkMuted * a), 0f);
                int clicked = ConfirmChoices(cx, midY, a, ref _confirmChoice);
                if (clicked == 0) PerformResetJourney();
                else if (clicked == 1) _confirmResetJourney = false;
                return;
            }

            string label = ActionButtonLabel(s);
            _sans.fontSize = Fs(13f);
            float lw = Measure(_sans, label);
            float bw = Mathf.Round(lw + S(32f)), bh = Mathf.Round(S(30f));
            var br = new Rect(Mathf.Round(right - bw), Mathf.Round(midY - bh * 0.5f), bw, bh);
            bool hov = Hover(br);
            if (hov || focus) Fill(br, WithA(Ink, 0.06f * a));
            Frame(br, WithA(Ink, (hov || focus ? 0.55f : HairlineStrongA) * a));
            TextMid(br.x + (bw - lw) * 0.5f, midY, label, _sans, WithA(Ink, InkStrong * a), 0f);
            if (Click(br)) ActivateSetting(s);
        }

        private void DrawKeybinds(Rect r, float a)
        {
            if (r.height < S(60f)) return;
            _sans.fontSize = Fs(12f);
            Text(new Rect(r.x, r.y, S(300f), S(18f)), "Keybinds", _sans, WithA(Ink, InkFaint * a), 0f);
            float y0 = r.y + S(28f), rowH = S(29f);
            int perCol = (Keybinds.Length + 1) / 2;
            float colW = (r.width - S(32f)) * 0.5f;
            for (int i = 0; i < Keybinds.Length; i++)
            {
                int col = i / perCol, row = i % perCol;
                float x = r.x + col * (colW + S(32f)), y = y0 + row * rowH;
                if (y + rowH > r.yMax + S(4f)) continue;
                float midY = y + rowH * 0.5f;
                _sans.fontSize = Fs(11f);
                float kw = Measure(_sans, Keybinds[i].key);
                var cap = new Rect(Mathf.Round(x), Mathf.Round(midY - S(10f)), Mathf.Round(Mathf.Max(S(22f), kw + S(12f))), Mathf.Round(S(20f)));
                Frame(cap, WithA(Ink, HairlineStrongA * a));
                TextMid(cap.x + (cap.width - kw) * 0.5f, midY, Keybinds[i].key, _sans, WithA(Ink, InkStrong * a), 0f);
                _sans.fontSize = Fs(13f);
                TextMid(x + S(104f), midY, Keybinds[i].action, _sans, WithA(Ink, InkMuted * a), 0f);
            }
        }

        private void HandleSettingsKeys()
        {
            var rows = TabRows[(int)_setTab];
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                bool back = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                int n = SettingsTabNames.Length;
                SelectSettingsTab((SettingsTab)(((int)_setTab + (back ? n - 1 : 1)) % n));
                return;
            }
            _setFocus = Mathf.Clamp(_setFocus, 0, rows.Length - 1);
            var s = rows[_setFocus];
            bool confirming = s == Setting.ResetJourney && _confirmResetJourney;

            int vstep = (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S) ? 1 : 0)
                      - (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W) ? 1 : 0);
            if (vstep != 0)
            {
                _setFocus = (_setFocus + vstep + rows.Length) % rows.Length;
                _confirmResetJourney = false;
                return;
            }

            int h = (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) ? 1 : 0)
                  - (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) ? 1 : 0);
            if (confirming)
            {
                if (h != 0) _confirmChoice = h < 0 ? 0 : 1;
                if (SubmitPressed())
                {
                    if (_confirmChoice == 0) PerformResetJourney();
                    else _confirmResetJourney = false;
                }
                return;
            }
            if (h != 0)
            {
                if (IsSlider(s))
                {
                    SliderRange(s, out _, out _, out _, out float keyStep);
                    bool coarse = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    SetSlider(s, GetSlider(s) + h * keyStep * (coarse ? 5f : 1f));
                }
                else if (!IsAction(s)) SetChoice(s, h < 0 ? 0 : 1);
            }
            else if (SubmitPressed())
            {
                if (IsAction(s)) ActivateSetting(s);
                else if (!IsSlider(s)) SetChoice(s, 1 - GetChoice(s));
            }
        }

        // ------------------------------------------------------------------ settings model
        private static readonly string[] SettingsTabNames = { "Display", "Controls", "Audio", "Save" };

        private static readonly Setting[][] TabRows =
        {
            new[] { Setting.WindowMode, Setting.VSync, Setting.Fov, Setting.Sun, Setting.Clouds, Setting.HudScale, Setting.ShowHud },
            new[] { Setting.MouseSensitivity, Setting.InvertY, Setting.CameraDistance },
            new[] { Setting.MasterVolume, Setting.Mute },
            new[] { Setting.SaveNow, Setting.ResetJourney, Setting.RestoreDefaults },
        };

        private static readonly (string key, string action)[] Keybinds =
        {
            ("WASD", "Move"), ("Shift", "Sprint"), ("Right-drag", "Look around"), ("Left-click", "Attack"),
            ("Space", "Jump"), ("C", "Roll"), ("E", "Interact, complete quest"), ("T", "Torch"),
            ("B", "Place campfire"), ("J", "Journal"), ("H", "Hide HUD"), ("V", "Fly mode"),
            ("[ / ]", "Time of day"), ("1–4", "Travel to landmarks"), ("Scroll", "Camera distance"), ("Esc", "Pause, back"),
        };

        private static bool IsSlider(Setting s) => s is Setting.Fov or Setting.Sun or Setting.HudScale or
            Setting.MouseSensitivity or Setting.CameraDistance or Setting.MasterVolume;
        private static bool IsAction(Setting s) => s is Setting.SaveNow or Setting.ResetJourney or Setting.RestoreDefaults;

        private static string SettingLabel(Setting s) => s switch
        {
            Setting.WindowMode => "Window mode",
            Setting.VSync => "VSync",
            Setting.Fov => "Field of view",
            Setting.Sun => "Time of day",
            Setting.Clouds => "Volumetric clouds",
            Setting.HudScale => "HUD scale",
            Setting.ShowHud => "Show HUD",
            Setting.MouseSensitivity => "Mouse sensitivity",
            Setting.InvertY => "Invert Y-axis",
            Setting.CameraDistance => "Camera distance",
            Setting.MasterVolume => "Master volume",
            Setting.Mute => "Mute audio",
            Setting.SaveNow => "Save journey",
            Setting.ResetJourney => "Reset journey",
            Setting.RestoreDefaults => "Restore defaults",
            _ => ""
        };

        private static string SettingHint(Setting s) => s switch
        {
            Setting.WindowMode => "Applies to standalone builds",
            Setting.VSync => "Sync frames to the display",
            Setting.Fov => "Vertical, in degrees",
            Setting.Sun => "Sun elevation; also [ and ]",
            Setting.Clouds => "HDRP clouds on the global volume",
            Setting.HudScale => "Compass, tracker and vitals",
            Setting.ShowHud => "Same as H",
            Setting.MouseSensitivity => "While holding the right mouse button",
            Setting.InvertY => "Push the mouse up to look down",
            Setting.CameraDistance => "Also the scroll wheel",
            Setting.MasterVolume => "All game audio",
            Setting.Mute => "Silence without losing the level",
            Setting.SaveNow => "Quests, campfires and progress",
            Setting.ResetJourney => "Start again from Oakhaven",
            Setting.RestoreDefaults => "Every setting on every tab",
            _ => ""
        };

        private static string ActionButtonLabel(Setting s) => s switch
        {
            Setting.SaveNow => "Save now",
            Setting.ResetJourney => "Reset",
            _ => "Restore"
        };

        /// min, max, drag snap, keyboard step
        private static void SliderRange(Setting s, out float min, out float max, out float snap, out float keyStep)
        {
            switch (s)
            {
                case Setting.Fov: min = WorldMillGameSettings.FovMin; max = WorldMillGameSettings.FovMax; snap = 1f; keyStep = 1f; break;
                case Setting.Sun: min = WorldMillGameSettings.SunMin; max = WorldMillGameSettings.SunMax; snap = 1f; keyStep = 1f; break;
                case Setting.HudScale: min = WorldMillGameSettings.HudScaleMin; max = WorldMillGameSettings.HudScaleMax; snap = 0.01f; keyStep = 0.05f; break;
                case Setting.MouseSensitivity: min = WorldMillGameSettings.SensMin; max = WorldMillGameSettings.SensMax; snap = 0.1f; keyStep = 0.1f; break;
                case Setting.CameraDistance: min = WorldMillGameSettings.CamDistMin; max = WorldMillGameSettings.CamDistMax; snap = 0.1f; keyStep = 0.5f; break;
                default: min = 0f; max = 1f; snap = 0.01f; keyStep = 0.05f; break;
            }
        }

        private float GetSlider(Setting s)
        {
            var c = _settings ?? _defaults ?? new WorldMillGameSettings();
            return s switch
            {
                Setting.Fov => c.fieldOfView,
                Setting.Sun => c.sunElevation,
                Setting.HudScale => c.hudScale,
                Setting.MouseSensitivity => c.mouseSensitivity,
                Setting.CameraDistance => c.cameraDistance,
                _ => c.masterVolume
            };
        }

        private void SetSlider(Setting s, float v)
        {
            if (_settings == null) return;
            SliderRange(s, out float min, out float max, out float snap, out _);
            v = Mathf.Clamp(Mathf.Round(v / snap) * snap, min, max);
            if (Mathf.Approximately(v, GetSlider(s))) return;
            switch (s)
            {
                case Setting.Fov: _settings.fieldOfView = v; break;
                case Setting.Sun: _settings.sunElevation = v; break;
                case Setting.HudScale: _settings.hudScale = v; break;
                case Setting.MouseSensitivity: _settings.mouseSensitivity = v; break;
                case Setting.CameraDistance: _settings.cameraDistance = v; break;
                default: _settings.masterVolume = v; break;
            }
            OnSettingChanged(s);
        }

        private static string SliderText(Setting s, float v) => s switch
        {
            Setting.Fov => Mathf.RoundToInt(v) + "°",
            Setting.Sun => Mathf.RoundToInt(v) + "°",
            Setting.HudScale => v.ToString("0.00") + "×",
            Setting.MouseSensitivity => v.ToString("0.0"),
            Setting.CameraDistance => v.ToString("0.0") + " m",
            _ => Mathf.RoundToInt(v * 100f) + "%"
        };

        private static void ChoiceLabels(Setting s, out string first, out string second)
        {
            if (s == Setting.WindowMode) { first = "Fullscreen"; second = "Windowed"; }
            else { first = "Off"; second = "On"; }
        }

        private int GetChoice(Setting s)
        {
            var c = _settings ?? _defaults ?? new WorldMillGameSettings();
            return s switch
            {
                Setting.WindowMode => c.fullscreen ? 0 : 1,
                Setting.VSync => c.vSync ? 1 : 0,
                Setting.Clouds => c.volumetricClouds ? 1 : 0,
                Setting.ShowHud => c.showHud ? 1 : 0,
                Setting.InvertY => c.invertY ? 1 : 0,
                Setting.Mute => c.muteAudio ? 1 : 0,
                _ => 0
            };
        }

        private void SetChoice(Setting s, int i)
        {
            if (_settings == null || GetChoice(s) == i) return;
            bool on = i == 1;
            switch (s)
            {
                case Setting.WindowMode: _settings.fullscreen = i == 0; break;
                case Setting.VSync: _settings.vSync = on; break;
                case Setting.Clouds: _settings.volumetricClouds = on; break;
                case Setting.ShowHud: _settings.showHud = on; break;
                case Setting.InvertY: _settings.invertY = on; break;
                case Setting.Mute: _settings.muteAudio = on; break;
                default: return;
            }
            OnSettingChanged(s);
        }

        private void ActivateSetting(Setting s)
        {
            switch (s)
            {
                case Setting.SaveNow:
                    FlushSettings();
                    if (rpgStore != null) rpgStore.SaveNow();
                    else ShowToast("Settings saved");
                    break;
                case Setting.ResetJourney:
                    if (!_confirmResetJourney) { _confirmResetJourney = true; _confirmChoice = 1; }
                    else PerformResetJourney();
                    break;
                case Setting.RestoreDefaults:
                    _settings = (_defaults ?? new WorldMillGameSettings()).Clone();
                    ApplyAllSettings();
                    MarkSettingsDirty();
                    ShowToast("Settings restored to defaults");
                    break;
            }
        }

        private void PerformResetJourney()
        {
            _confirmResetJourney = false;
            if (rpgStore != null) rpgStore.ResetSave();
            if (playerController != null) playerController.ResetToSpawn();
        }

        /// Live-applies one setting, then schedules a debounced write.
        private void OnSettingChanged(Setting s)
        {
            switch (s)
            {
                case Setting.WindowMode:
                case Setting.VSync: _settings.ApplyDisplay(); break;
                case Setting.Clouds: _settings.ApplyClouds(); break;
                case Setting.ShowHud:
                    _hudVisible = _settings.showHud;
                    if (!_hudVisible) SetCodex(false);
                    break;
                case Setting.MasterVolume:
                case Setting.Mute: _settings.ApplyAudio(); break;
                case Setting.HudScale: break; // read every OnGUI
                default: ApplyPlayerSettings(); break;
            }
            MarkSettingsDirty();
        }

        private void ApplyAllSettings()
        {
            if (_settings == null) return;
            _settings.ApplyDisplay();
            _settings.ApplyAudio();
            _settings.ApplyClouds();
            _hudVisible = _settings.showHud;
            ApplyPlayerSettings();
        }

        private void ApplyPlayerSettings()
        {
            if (_settings == null) return;
            var cam = PlayerCamera;
            if (cam != null) cam.fieldOfView = _settings.fieldOfView;
            if (playerController == null) return;
            playerController.mouseSensitivity = _settings.mouseSensitivity;
            playerController.invertY = _settings.invertY;
            playerController.cameraDistance = _settings.cameraDistance;
            if (!Mathf.Approximately(playerController.sunElevationDeg, _settings.sunElevation) || !_appliedToPlayer)
                playerController.SetSunElevation(_settings.sunElevation);
            _appliedToPlayer = true;
        }

        /// [ / ] and the scroll wheel change these in play; reflect them when Settings opens.
        private void SyncLiveValuesIntoSettings()
        {
            if (_settings == null || playerController == null) return;
            float sun = Mathf.Clamp(playerController.sunElevationDeg, WorldMillGameSettings.SunMin, WorldMillGameSettings.SunMax);
            float dist = Mathf.Clamp(playerController.cameraDistance, WorldMillGameSettings.CamDistMin, WorldMillGameSettings.CamDistMax);
            if (!Mathf.Approximately(sun, _settings.sunElevation) || !Mathf.Approximately(dist, _settings.cameraDistance))
            {
                _settings.sunElevation = Mathf.Round(sun);
                _settings.cameraDistance = Mathf.Round(dist * 10f) / 10f;
                MarkSettingsDirty();
            }
        }

        /// Scene-authored values become "defaults", so Restore defaults returns to what the scene shipped with.
        private WorldMillGameSettings CaptureSceneDefaults()
        {
            var d = new WorldMillGameSettings
            {
                fullscreen = Screen.fullScreen,
                vSync = QualitySettings.vSyncCount > 0,
                showHud = true,
                hudScale = 1f,
                masterVolume = 1f,
                muteAudio = false,
                volumetricClouds = WorldMillGameSettings.GetVolumetricClouds() ?? true,
            };
            var cam = PlayerCamera;
            if (cam != null) d.fieldOfView = cam.fieldOfView;
            if (playerController != null)
            {
                d.sunElevation = playerController.sunElevationDeg;
                d.mouseSensitivity = playerController.mouseSensitivity;
                d.invertY = playerController.invertY;
                d.cameraDistance = playerController.cameraDistance;
            }
            d.Clamp();
            return d;
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
            _settingsDirtyAt = Time.unscaledTime;
        }

        private void FlushSettings()
        {
            if (!_settingsDirty || _settings == null) return;
            _settingsDirty = false;
            _settings.Write();
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

        /// Draws single-line text vertically centred on midY.
        private void TextMid(float x, float midY, string s, GUIStyle st, Color c, float shadow)
        {
            if (string.IsNullOrEmpty(s)) return;
            var size = st.CalcSize(Tmp(s));
            float h = Mathf.Ceil(size.y);
            Text(new Rect(Mathf.Round(x), Mathf.Round(midY - h * 0.5f), Mathf.Ceil(size.x) + 2f, h), s, st, c, shadow);
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

        /// 256×1 left-to-right scrim ramp: solid at the left edge, gone by the right edge.
        private static Texture2D BuildScrimTexture()
        {
            const int w = 256;
            var t = new Texture2D(w, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[w];
            for (int i = 0; i < w; i++)
            {
                float u = i / (w - 1f);
                float aa = 1f - Mathf.SmoothStep(0f, 1f, u);
                aa *= aa * 0.35f + 0.65f;   // a touch more fall-off in the middle
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
