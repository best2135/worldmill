using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill.UI
{
    /// <summary>
    /// Player-facing settings for the WorldMill RPG, persisted as JSON next to the RPG save:
    /// Path.Combine(Application.persistentDataPath, "worldmill_settings.json").
    ///
    /// Pure data + engine-global appliers (display, audio, HDRP volumetric clouds). Anything that
    /// touches the player, camera or HUD is applied by WorldMillMinimalHud, which owns the menus.
    /// </summary>
    [Serializable]
    public sealed class WorldMillGameSettings
    {
        public const string FileName = "worldmill_settings.json";

        // Ranges — mirrored in docs/ui-tokens.json → menus.settings
        public const float FovMin = 50f, FovMax = 90f;
        public const float SunMin = 4f, SunMax = 80f;
        public const float HudScaleMin = 0.80f, HudScaleMax = 1.30f;
        public const float SensMin = 0.5f, SensMax = 6.0f;
        public const float CamDistMin = 3.0f, CamDistMax = 14.0f;

        public int version = 1;

        // Display
        public bool fullscreen = true;
        public bool vSync = true;
        public float fieldOfView = 62f;
        public float sunElevation = 26f;
        public bool volumetricClouds = true;
        public float hudScale = 1f;
        public bool showHud = true;

        // Controls
        public float mouseSensitivity = 2.6f;
        public bool invertY = false;
        public float cameraDistance = 6.2f;

        // Audio
        public float masterVolume = 1f;
        public bool muteAudio = false;

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);
        public static bool FileExists => File.Exists(FilePath);

        public WorldMillGameSettings Clone() => (WorldMillGameSettings)MemberwiseClone();

        public void Clamp()
        {
            fieldOfView = Mathf.Clamp(fieldOfView, FovMin, FovMax);
            sunElevation = Mathf.Clamp(sunElevation, SunMin, SunMax);
            hudScale = Mathf.Clamp(hudScale, HudScaleMin, HudScaleMax);
            mouseSensitivity = Mathf.Clamp(mouseSensitivity, SensMin, SensMax);
            cameraDistance = Mathf.Clamp(cameraDistance, CamDistMin, CamDistMax);
            masterVolume = Mathf.Clamp01(masterVolume);
        }

        /// Loads the settings file over <paramref name="defaults"/>; missing or corrupt files keep the defaults.
        public static WorldMillGameSettings LoadOr(WorldMillGameSettings defaults)
        {
            var s = defaults != null ? defaults.Clone() : new WorldMillGameSettings();
            try
            {
                if (File.Exists(FilePath))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), s);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldMill] Could not read settings, using defaults: {e.Message}");
                s = defaults != null ? defaults.Clone() : new WorldMillGameSettings();
            }
            s.Clamp();
            return s;
        }

        public bool Write()
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldMill] Failed to save settings: {e.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------ engine-global appliers
        public void ApplyDisplay()
        {
            if (Screen.fullScreen != fullscreen) Screen.fullScreen = fullscreen;
            int vs = vSync ? 1 : 0;
            if (QualitySettings.vSyncCount != vs) QualitySettings.vSyncCount = vs;
        }

        public void ApplyAudio()
        {
            AudioListener.volume = muteAudio ? 0f : Mathf.Clamp01(masterVolume);
        }

        public void ApplyClouds() => SetVolumetricClouds(volumetricClouds);

        // Only the runtime (instantiated) profile's override is cached; Unity's null check
        // drops it automatically when the scene unloads.
        private static VolumetricClouds _runtimeClouds;

        /// Reads the VolumetricClouds override on the first global HDRP Volume that has one.
        /// Returns null when no such override exists (the toggle then does nothing).
        public static bool? GetVolumetricClouds()
        {
            var c = FindClouds(false);
            return c != null ? c.enable.value : (bool?)null;
        }

        /// Toggles the VolumetricClouds override on the global HDRP Volume, guarded:
        /// works on the Volume's runtime profile instance, never the shared asset on disk.
        public static bool SetVolumetricClouds(bool on)
        {
            try
            {
                var current = FindClouds(false);
                if (current == null) return false;
                if (current.enable.overrideState && current.enable.value == on) return true;
                var c = FindClouds(true);
                if (c == null) return false;
                c.enable.Override(on);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WorldMill] Could not toggle volumetric clouds: {e.Message}");
                return false;
            }
        }

        private static VolumetricClouds FindClouds(bool instantiate)
        {
            if (_runtimeClouds != null) return _runtimeClouds;
            var volumes = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            foreach (var v in volumes)
            {
                if (v == null || !v.isGlobal) continue;
                bool runtime = instantiate || v.HasInstantiatedProfile();
                VolumeProfile p = runtime ? v.profile : v.sharedProfile;
                if (p == null || !p.TryGet(out VolumetricClouds c)) continue;
                if (runtime) _runtimeClouds = c;
                return c;
            }
            return null;
        }
    }
}
