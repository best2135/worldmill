using UnityEngine;

namespace WorldMill
{
    /// <summary>
    /// Smooth Explorer Fly Camera, Sun Time-of-Day controller, Water Follower, and Runtime HUD.
    /// </summary>
    public sealed class WorldMillFlyCamera : MonoBehaviour
    {
        public WorldMillStreamer streamer;
        public Light sunLight;
        public Transform waterPlane;

        public float baseSpeed = 28f;
        public float boostSpeed = 85f;
        public float mouseSensitivity = 2.4f;
        public float sunElevationDeg = 38f;

        private float _yaw = 0f;
        private float _pitch = 14f;
        private float _fpsSmooth = 60f;

        private void Start()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
            ApplySunAngle();
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f)
                _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / dt, 0.1f);

            // Hold RMB or LMB to look
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

            // Teleport presets (remember Z is mirrored vs Rust RH coordinates)
            if (Input.GetKeyDown(KeyCode.Alpha1)) Teleport(new Vector3(12f, 36f, -48f), 0f, 14f);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Teleport(new Vector3(40f, 58f, -10f), -42f, 20f);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Teleport(new Vector3(0f, -18f, 0f), 25f, 4f);

            // Sun angle [ / ]
            if (Input.GetKey(KeyCode.LeftBracket))
            {
                sunElevationDeg = Mathf.Clamp(sunElevationDeg - 28f * dt, 5f, 80f);
                ApplySunAngle();
            }
            if (Input.GetKey(KeyCode.RightBracket))
            {
                sunElevationDeg = Mathf.Clamp(sunElevationDeg + 28f * dt, 5f, 80f);
                ApplySunAngle();
            }

            // LOD tint toggle (L) and new world seed (N)
            if (streamer != null && Input.GetKeyDown(KeyCode.L))
                streamer.SetLodTint(!streamer.LodTint);
            if (streamer != null && Input.GetKeyDown(KeyCode.N))
                streamer.ChangeSeed(streamer.worldSeed + 17);

            // Keep water plane centered under camera
            if (waterPlane != null)
            {
                var wp = waterPlane.position;
                waterPlane.position = new Vector3(transform.position.x, wp.y, transform.position.z);
            }
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
            float t = Mathf.InverseLerp(5f, 80f, sunElevationDeg);
            float sunset = 1f - Mathf.Pow(t, 0.65f);
            if (sunLight != null)
            {
                sunLight.transform.rotation = Quaternion.Euler(sunElevationDeg, -35f + t * 65f, 0f);
                sunLight.color = Color.Lerp(new Color(1.0f, 0.96f, 0.88f), new Color(1.0f, 0.54f, 0.24f), sunset);
                sunLight.intensity = Mathf.Lerp(1.6f, 3.0f, t);
            }
            RenderSettings.fogColor = Color.Lerp(new Color(0.64f, 0.78f, 0.92f), new Color(0.92f, 0.44f, 0.22f), sunset);
            DynamicGI.UpdateEnvironment();
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(14, 14, 470, 128), "");
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = true
            };
            string gpuInfo = streamer != null && streamer.LatestStats != null
                ? $"{streamer.LatestStats.adapter} ({streamer.LatestStats.backend}, {streamer.LatestStats.worker_slots} workers) | Bake: {streamer.LatestStats.avg_gpu_ms:F2} ms | Hits: {streamer.LatestStats.cache_hits}"
                : (streamer != null ? streamer.ConnectionStatus : "No streamer");

            string text =
                $"<b>WorldMill — Unity 6 URP + Rust wgpu Backend</b>   |   <b>{Mathf.RoundToInt(_fpsSmooth)} FPS</b>   |   Seed: {(streamer != null ? streamer.worldSeed : 0)}\n" +
                $"<color=#93c5fd>{gpuInfo}</color>\n" +
                $"Chunks: {(streamer != null ? streamer.ActiveChunkCount : 0)}   |   Triangles: {(streamer != null ? streamer.ActiveTriangleCount.ToString("N0") : "0")}   |   Sun: {sunElevationDeg:F0}°\n" +
                $"Pos: ({transform.position.x:F1}, {transform.position.y:F1}, {transform.position.z:F1})\n" +
                $"<color=#cbd5e1>Drag Mouse: Look · WASD/Space/Shift: Fly · F: Boost · 1/2/3: Presets · [/]: Sun · L: LOD · N: Seed</color>";

            GUI.Label(new Rect(26, 22, 450, 114), text, style);
        }
    }
}
