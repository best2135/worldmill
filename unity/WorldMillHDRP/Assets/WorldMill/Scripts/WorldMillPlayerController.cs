using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using WorldMill.UI;

namespace WorldMill
{
    /// <summary>
    /// Self-contained Third-Person RPG Explorer Controller for the eroded Unity 6 HDRP world.
    /// Zero external server dependency — grounds instantaneously on TerrainCollider.
    /// </summary>
    public sealed class WorldMillPlayerController : MonoBehaviour
    {
        [Header("Scene Links")]
        public Light sunLight;
        public Camera playerCamera;
        public WorldMillLocalRpgStore rpgStore;
        public WorldMillMinimalHud minimalHud;
        public GameObject characterModelPrefab;

        [Header("Movement & Physics")]
        public float walkSpeed = 7.5f;
        public float sprintSpeed = 14.5f;
        public float swimSpeed = 6.2f;
        public float flySpeed = 36f;
        public float flyBoostSpeed = 110f;
        public float jumpForce = 8.4f;
        public float gravity = 22f;
        public float waterLevel = 0.0f;

        [Header("Camera")]
        public float cameraDistance = 6.2f;
        public float cameraHeight = 1.95f;
        public float mouseSensitivity = 2.6f;

        [Header("Sun")]
        public float sunElevationDeg = 26f;

        public float CurrentHp { get; private set; } = 100f;
        public float MaxHp => rpgStore != null && rpgStore.Save != null ? rpgStore.Save.maxHp : 100f;
        public float CurrentStamina { get; private set; } = 100f;
        public float MaxStamina => rpgStore != null && rpgStore.Save != null ? rpgStore.Save.maxStamina : 100f;
        public bool TorchLit { get; private set; } = false;
        public bool IsSwimming { get; private set; } = false;
        public bool IsFlyMode { get; private set; } = false;

        private CharacterController _cc;
        private HDAdditionalLightData _hdSun;
        private Transform _avatarRoot;
        private Animator _animator;
        private Transform _torchObj;
        private Light _torchLight;
        private HDAdditionalLightData _hdTorch;

        private float _yaw = 0f;
        private float _pitch = 14f;
        private float _verticalVel = 0f;
        private float _rollTimer = 0f;
        private Vector3 _rollDir;
        private float _currentHorizSpeed = 0f;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            if (_cc == null)
            {
                _cc = gameObject.AddComponent<CharacterController>();
                _cc.height = 1.85f;
                _cc.radius = 0.42f;
                _cc.center = new Vector3(0f, 0.925f, 0f);
                _cc.stepOffset = 0.55f;
                _cc.slopeLimit = 52f;
            }
            if (characterModelPrefab != null)
                SpawnRiggedCharacter();
        }

        private void Start()
        {
            if (playerCamera == null) playerCamera = Camera.main;
            if (sunLight != null) _hdSun = sunLight.GetComponent<HDAdditionalLightData>();
            CurrentHp = MaxHp;
            CurrentStamina = MaxStamina;
            SnapToTerrain(transform.position);
            ApplySunAngle();
        }

        private void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            bool codexOpen = minimalHud != null && minimalHud.IsCodexOpen;

            if (!codexOpen)
            {
                HandleMetaControls(dt);
                HandleCameraLook(dt);

                if (IsFlyMode) UpdateFlyMovement(dt);
                else UpdateThirdPersonMovement(dt);
            }

            UpdateCameraTransform();
            UpdateTorchVisuals();
            SyncHudVitals();
        }

        private void SyncHudVitals()
        {
            if (minimalHud == null) return;
            minimalHud.SetVitals(CurrentHp, MaxHp, CurrentStamina, MaxStamina);
            minimalHud.TorchLit = TorchLit;
            minimalHud.IsSwimming = IsSwimming;
            minimalHud.IsFlyMode = IsFlyMode;
            minimalHud.SunElevationDeg = sunElevationDeg;
        }

        private void HandleMetaControls(float dt)
        {
            if (Input.GetKeyDown(KeyCode.V))
            {
                IsFlyMode = !IsFlyMode;
                _verticalVel = 0f;
                if (_avatarRoot != null) _avatarRoot.gameObject.SetActive(!IsFlyMode);
            }

            if (Input.GetKeyDown(KeyCode.T))
            {
                TorchLit = !TorchLit;
                if (_torchObj != null) _torchObj.gameObject.SetActive(TorchLit);
            }

            // Build persistent Campfire (B)
            if (Input.GetKeyDown(KeyCode.B) && rpgStore != null)
            {
                Vector3 ahead = transform.position + transform.forward * 2.5f;
                rpgStore.PlaceCampfireAt(ahead, transform.eulerAngles.y);
            }

            // Teleport landmarks: 1 = Oakhaven Hamlet, 2 = River Bridge, 3 = Rune Sanctuary, 4 = Northern Summit
            if (Input.GetKeyDown(KeyCode.Alpha1)) TeleportXZ(512f, 438f, 0f, 12f);
            if (Input.GetKeyDown(KeyCode.Alpha2)) TeleportXZ(466f, 492f, -90f, 14f);
            if (Input.GetKeyDown(KeyCode.Alpha3)) TeleportXZ(618f, 574f, 0f, 12f);
            if (Input.GetKeyDown(KeyCode.Alpha4)) TeleportXZ(540f, 748f, 0f, 16f);

            // Sun Time-of-Day [ / ]
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
        }

        public void TeleportXZ(float x, float z, float yaw, float pitch)
        {
            SnapToTerrain(new Vector3(x, 0f, z));
            _yaw = yaw;
            _pitch = pitch;
            _verticalVel = 0f;
        }

        private void SnapToTerrain(Vector3 pos)
        {
            float y = pos.y;
            if (Terrain.activeTerrain != null)
                y = Terrain.activeTerrain.SampleHeight(pos) + Terrain.activeTerrain.transform.position.y + 0.15f;
            _cc.enabled = false;
            transform.position = new Vector3(pos.x, y, pos.z);
            _cc.enabled = true;
        }

        private void HandleCameraLook(float dt)
        {
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -75f, 80f);
            }
            if (Input.GetMouseButtonDown(0) && !IsFlyMode && _animator != null && CurrentStamina >= 12f)
            {
                CurrentStamina = Mathf.Max(0f, CurrentStamina - 12f);
                _animator.SetTrigger("Attack");
            }
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f && !IsFlyMode)
                cameraDistance = Mathf.Clamp(cameraDistance - scroll * 0.7f, 2.5f, 16f);
        }

        private void UpdateFlyMovement(float dt)
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 fwd = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;
            float speed = (Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.LeftShift) ? flyBoostSpeed : flySpeed) * dt;

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += fwd;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= fwd;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += right;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move -= right;
            if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.Q)) move += Vector3.down;

            if (move.sqrMagnitude > 0f)
                transform.position += move.normalized * speed;
        }

        private void UpdateThirdPersonMovement(float dt)
        {
            float inX = 0f, inZ = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) inZ += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) inZ -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) inX += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) inX -= 1f;

            Quaternion yawRot = Quaternion.Euler(0f, _yaw, 0f);
            Vector3 moveDir = yawRot * new Vector3(inX, 0f, inZ);
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

            IsSwimming = transform.position.y < waterLevel - 0.65f;

            if (!IsSwimming && _rollTimer <= 0f && _cc.isGrounded && CurrentStamina >= 22f &&
                (Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.LeftControl)))
            {
                _rollTimer = 0.36f;
                _rollDir = moveDir.sqrMagnitude > 0.01f ? moveDir : transform.forward;
                CurrentStamina = Mathf.Max(0f, CurrentStamina - 22f);
                if (_animator != null) _animator.SetTrigger("Roll");
            }

            bool wantsSprint = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.F)) && moveDir.sqrMagnitude > 0.01f && CurrentStamina > 2f;
            Vector3 horiz;

            if (_rollTimer > 0f)
            {
                _rollTimer -= dt;
                horiz = _rollDir * (sprintSpeed * 1.45f);
            }
            else if (IsSwimming)
            {
                horiz = moveDir * (wantsSprint ? swimSpeed * 1.4f : swimSpeed);
                CurrentStamina = wantsSprint
                    ? Mathf.Max(0f, CurrentStamina - 14f * dt)
                    : Mathf.Min(MaxStamina, CurrentStamina + 14f * dt);
            }
            else
            {
                horiz = moveDir * (wantsSprint ? sprintSpeed : walkSpeed);
                CurrentStamina = wantsSprint
                    ? Mathf.Max(0f, CurrentStamina - 18f * dt)
                    : Mathf.Min(MaxStamina, CurrentStamina + 22f * dt);
            }

            if (IsSwimming)
            {
                float targetY = waterLevel - 1.05f;
                float buoy = (targetY - transform.position.y) * 5.0f;
                if (Input.GetKey(KeyCode.Space)) buoy = 4.5f;
                else if (Input.GetKey(KeyCode.Q)) buoy = -4.0f;
                _verticalVel = Mathf.Lerp(_verticalVel, buoy, 8f * dt);
            }
            else
            {
                if (_cc.isGrounded && _verticalVel < 0f) _verticalVel = -2.0f;
                if (_cc.isGrounded && Input.GetKeyDown(KeyCode.Space) && CurrentStamina >= 10f)
                {
                    _verticalVel = jumpForce;
                    CurrentStamina = Mathf.Max(0f, CurrentStamina - 10f);
                    if (_animator != null) _animator.SetTrigger("Jump");
                }
                _verticalVel -= gravity * dt;
            }

            _cc.Move((horiz + Vector3.up * _verticalVel) * dt);
            _currentHorizSpeed = new Vector2(horiz.x, horiz.z).magnitude;

            if (horiz.sqrMagnitude > 0.05f)
            {
                Quaternion targetRot = Quaternion.LookRotation(new Vector3(horiz.x, 0f, horiz.z).normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 12f * dt);
            }

            if (_animator != null)
            {
                _animator.SetFloat("Speed", _currentHorizSpeed);
                _animator.SetBool("IsGrounded", _cc.isGrounded);
                _animator.SetBool("IsSwimming", IsSwimming);
            }
        }

        private void UpdateCameraTransform()
        {
            if (playerCamera == null) return;
            Quaternion camRot = Quaternion.Euler(_pitch, _yaw, 0f);

            if (IsFlyMode)
            {
                playerCamera.transform.position = transform.position + Vector3.up * 1.6f;
                playerCamera.transform.rotation = camRot;
                return;
            }

            Vector3 pivot = transform.position + Vector3.up * cameraHeight;
            Vector3 desiredOffset = camRot * new Vector3(0f, 0f, -cameraDistance);
            Vector3 camPos = pivot + desiredOffset;

            if (Physics.SphereCast(pivot, 0.28f, desiredOffset.normalized, out RaycastHit hit, cameraDistance))
                camPos = pivot + desiredOffset.normalized * Mathf.Max(1.0f, hit.distance - 0.15f);

            playerCamera.transform.position = camPos;
            playerCamera.transform.rotation = camRot;
        }

        private void UpdateTorchVisuals()
        {
            if (!TorchLit || _torchLight == null) return;
            float flicker = 0.88f + 0.14f * Mathf.Sin(Time.time * 17.3f) + 0.08f * Mathf.Cos(Time.time * 29.1f);
            if (_hdTorch != null) _hdTorch.SetIntensity(650f * flicker, LightUnit.Lumen);
            else _torchLight.intensity = 650f * flicker;
        }

        private void ApplySunAngle()
        {
            if (sunLight == null) return;
            float t = Mathf.InverseLerp(4f, 80f, sunElevationDeg);
            sunLight.transform.rotation = Quaternion.Euler(sunElevationDeg, -38f + t * 72f, 0f);
            sunLight.useColorTemperature = true;
            sunLight.colorTemperature = Mathf.Lerp(3200f, 5900f, Mathf.Pow(t, 0.55f));
            float lux = Mathf.Lerp(28000f, 115000f, Mathf.Pow(t, 0.5f));
            if (_hdSun != null) _hdSun.SetIntensity(lux, LightUnit.Lux);
            else sunLight.intensity = lux;
        }

        private void SpawnRiggedCharacter()
        {
            var inst = Instantiate(characterModelPrefab, transform);
            inst.name = "RiggedHero";
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            _avatarRoot = inst.transform;
            _animator = inst.GetComponentInChildren<Animator>();

            var lit = Shader.Find("HDRP/Lit");
            _torchObj = new GameObject("HandheldTorch").transform;
            _torchObj.SetParent(_avatarRoot, false);
            _torchObj.localPosition = new Vector3(0.45f, 1.45f, 0.35f);
            if (lit != null)
            {
                var flameMat = new Material(lit);
                flameMat.SetColor("_BaseColor", new Color(1.0f, 0.55f, 0.15f));
                flameMat.SetColor("_EmissiveColor", new Color(4.5f, 1.8f, 0.35f));
                flameMat.SetColor("_EmissiveColorLDR", new Color(4.5f, 1.8f, 0.35f));
                flameMat.SetFloat("_UseEmissiveIntensity", 1f);
                flameMat.SetFloat("_EmissiveIntensity", 8f);
                HDMaterial.ValidateMaterial(flameMat);

                var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(orb.GetComponent<Collider>());
                orb.transform.SetParent(_torchObj, false);
                orb.transform.localScale = Vector3.one * 0.16f;
                orb.GetComponent<MeshRenderer>().sharedMaterial = flameMat;
            }
            var lightGo = new GameObject("TorchHDRPLight");
            lightGo.transform.SetParent(_torchObj, false);
            _torchLight = lightGo.AddComponent<Light>();
            _torchLight.type = LightType.Point;
            _torchLight.color = new Color(1.0f, 0.62f, 0.24f);
            _torchLight.range = 24f;
            _hdTorch = lightGo.AddComponent<HDAdditionalLightData>();
            _hdTorch.SetIntensity(650f, LightUnit.Lumen);
            _hdTorch.EnableShadows(true);
            _torchObj.gameObject.SetActive(TorchLit);
        }
    }
}
