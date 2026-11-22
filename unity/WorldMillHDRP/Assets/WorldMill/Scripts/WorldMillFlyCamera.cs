using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace WorldMill
{
    /// <summary>
    /// Explorer Fly Camera + Physical HDRP Sun Controller (auto-updates Physically Based Sky,
    /// Volumetric Clouds, and Volumetric Fog god-rays as you scrub the sun angle with [ / ]).
    /// </summary>
    public sealed class WorldMillFlyCamera : MonoBehaviour
    {
        public WorldMillStreamer streamer;
        public Light sunLight;

        public float baseSpeed = 28f;
        public float boostSpeed = 88f;
        public float mouseSensitivity = 2.4f;
        public float sunElevationDeg = 28f;

        private float _yaw = 0f;
        private float _pitch = 14f;
        private float _fpsSmooth = 60f;
        private HDAdditionalLightData _hdSun;

        private void Start()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
            if (sunLight != null)
                _hdSun = sunLight.GetComponent<HDAdditionalLightData>();
            ApplySunAngle();
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f)
                _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / dt, 0.1f);

            if (Input.GetMouseButton(1) || Input.GetMouseButton(0))
            {
                _yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -85f, 85f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float speed = (Input.GetKey(KeyCode.F) ? boostSpeed : baseSpeed) * dt;
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += transform.forward;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= transform.forward;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += transform.right;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move -= transform.right;
            if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.Q)) move += Vector3.down;

            if (move.sqrMagnitude > 0f)
                transform.position += move.normalized * speed;

            // Teleport presets (1 = Alpine Vista, 2 = Sky Arches, 3 = Crystal Caves)
            if (Input.GetKeyDown(KeyCode.Alpha1)) Teleport(new Vector3(12f, 36f, -48f), 0f, 14f);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Teleport(new Vector3(40f, 58f, -10f), -42f, 20f);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Teleport(new Vector3(0f, -18f, 0f), 25f, 4f);

            if (Input.GetKey(KeyCode.LeftBracket))
            {
                sunElevationDeg = Mathf.Clamp(sunElevationDeg - 26f * dt, 4f, 80f);
                ApplySunAngle();
            }
            if (Input.GetKey(KeyCode.RightBracket))
            {
                sunElevationDeg = Mathf.Clamp(sunElevationDeg + 26f * dt, 4f, 80f);
                ApplySunAngle();
            }

            if (streamer != null && Input.GetKeyDown(KeyCode.N))
                streamer.ChangeSeed(streamer.worldSeed + 17);
        }

        private void Teleport(Vector3 pos, float yaw, float pitch)
        {
            transform.position = pos;
            _yaw = yaw;
            _pitch = pitch;
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            streamer?.ForceSendPose();
        }

        private void ApplySunAngle()
        {
            if (sunLight == null) return;
            float t = Mathf.InverseLerp(4f, 80f, sunElevationDeg);
            sunLight.transform.rotation = Quaternion.Euler(sunElevationDeg, -38f + t * 72f, 0f);

            // Physical color temperature & lux (golden sunset -> crisp midday)
            float kelvin = Mathf.Lerp(3200f, 5900f, Mathf.Pow(t, 0.55f));
            float lux = Mathf.Lerp(28000f, 115000f, Mathf.Pow(t, 0.5f));
            sunLight.useColorTemperature = true;
            sunLight.colorTemperature = kelvin;
            if (_hdSun != null)
                _hdSun.SetIntensity(lux, LightUnit.Lux);
            else
                sunLight.intensity = lux;
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(14, 14, 510, 126), "");
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = true
            };
            string gpuInfo = streamer != null && streamer.LatestStats != null
                ? $"{streamer.LatestStats.adapter} ({streamer.LatestStats.backend}, {streamer.LatestStats.worker_slots} workers) | Bake: {streamer.LatestStats.avg_gpu_ms:F2} ms | Hits: {streamer.LatestStats.cache_hits}"
                : (streamer != null ? streamer.ConnectionStatus : "No streamer");

            string text =
                $"<b>WorldMill — Unity 6 HDRP + Rust wgpu Backend</b>   |   <b>{Mathf.RoundToInt(_fpsSmooth)} FPS</b>   |   Seed: {(streamer != null ? streamer.worldSeed : 0)}\n" +
                $"<color=#7dd3fc>{gpuInfo}</color>\n" +
                $"Chunks: {(streamer != null ? streamer.ActiveChunkCount : 0)}   |   Triangles: {(streamer != null ? streamer.ActiveTriangleCount.ToString("N0") : "0")}   |   Sun: {sunElevationDeg:F0}°\n" +
                $"Pos: ({transform.position.x:F1}, {transform.position.y:F1}, {transform.position.z:F1})  ·  Physically Based Sky + Volumetric Clouds + HDRP Ocean\n" +
                $"<color=#cbd5e1>Drag Mouse: Look · WASD/Space/Shift: Fly · F: Boost · 1/2/3: Presets · [/]: Sun · N: Seed</color>";

            GUI.Label(new Rect(26, 22, 490, 112), text, style);
        }
    }
}
