using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using WorldMill.UI;

namespace WorldMill
{
    /// <summary>
    /// Witcher 3 / Valheim-style Third-Person RPG Explorer Controller with:
    /// - Safe terrain grounding once streamed chunk MeshColliders arrive
    /// - Walk, Sprint (stamina), Jump, Dodge Roll, and River/Ocean Swimming (y < -5.8)
    /// - Toggleable Handheld HDRP Torch (T) with warm flickering shadow light
    /// - Persistent Campfire Placement (B) synced to Rust backend
    /// - Third-person collision-aware orbit camera + Free-Fly toggle (V)
    /// - Physical HDRP Sun angle scrubber ([ / ]) & Teleport presets (1/2/3)
    /// </summary>
    public sealed class WorldMillPlayerController : MonoBehaviour
    {
        [Header("Scene Links")]
        public WorldMillStreamer streamer;
        public Light sunLight;
        public Camera playerCamera;
        public WorldMillRpgClient rpgClient;
        public WorldMillRpgHud rpgHud;
        public GameObject characterModelPrefab;

        [Header("Movement & Physics")]
        public float walkSpeed = 7.5f;
        public float sprintSpeed = 14.5f;
        public float swimSpeed = 6.2f;
        public float flySpeed = 32f;
        public float flyBoostSpeed = 90f;
        public float jumpForce = 8.4f;
        public float gravity = 22f;
        public float waterLevel = -6.0f;

        [Header("Camera")]
        public float cameraDistance = 6.5f;
        public float cameraHeight = 2.0f;
        public float mouseSensitivity = 2.6f;

        [Header("Sun")]
        public float sunElevationDeg = 28f;

        // Live RPG vitals read by WorldMillRpgHud
        public float CurrentHp { get; private set; } = 100f;
        public float MaxHp { get; private set; } = 100f;
        public float CurrentStamina { get; private set; } = 100f;
        public float MaxStamina { get; private set; } = 100f;
        public bool TorchLit { get; private set; } = false;
        public bool IsSwimming { get; private set; } = false;
        public bool IsFlyMode { get; private set; } = false;
        public bool GroundedReady { get; private set; } = false;
        public float SunElevationDeg => sunElevationDeg;

        private CharacterController _cc;
        private HDAdditionalLightData _hdSun;
        private Transform _avatarRoot;
        private Animator _animator;
        private Transform _leftLeg;
        private Transform _rightLeg;
        private Transform _torchObj;
        private Light _torchLight;
        private HDAdditionalLightData _hdTorch;

        private float _yaw = 0f;
        private float _pitch = 16f;
        private float _verticalVel = 0f;
        private float _rollTimer = 0f;
        private Vector3 _rollDir;
        private float _walkCycle = 0f;
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
            else
                BuildStylizedAdventurerAvatar();
        }

        private void Start()
        {
            if (playerCamera == null) playerCamera = Camera.main;
            if (sunLight != null) _hdSun = sunLight.GetComponent<HDAdditionalLightData>();
            ApplySunAngle();
        }

        private void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            bool codexOpen = rpgHud != null && rpgHud.IsCodexOpen;

            if (!codexOpen)
            {
                HandleMetaControls(dt);
                HandleCameraLook(dt);

                if (IsFlyMode)
                {
                    UpdateFlyMovement(dt);
                }
                else
                {
                    EnsureSafeGroundSpawn();
                    if (GroundedReady)
                        UpdateThirdPersonMovement(dt);
                }
            }

            UpdateCameraTransform();
            UpdateTorchVisuals();
            SyncHudVitals();
        }

        private void SyncHudVitals()
        {
            if (rpgHud == null) return;
            rpgHud.SetVitals(CurrentHp, MaxHp, CurrentStamina, MaxStamina);
            rpgHud.TorchLit = TorchLit;
            rpgHud.IsSwimming = IsSwimming;
            rpgHud.IsFlyMode = IsFlyMode;
            rpgHud.SunElevationDeg = sunElevationDeg;
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

            // Build persistent HDRP Campfire right in front of the player (B)
            if (Input.GetKeyDown(KeyCode.B) && rpgClient != null)
            {
                Vector3 ahead = transform.position + transform.forward * 2.6f;
                Vector3 firePos = ahead;
                if (Physics.Raycast(ahead + Vector3.up * 6f, Vector3.down, out RaycastHit hit, 18f))
                    firePos = hit.point;
                uint seed = streamer != null ? streamer.worldSeed : 42u;
                rpgClient.PlaceCampfireAt(firePos, transform.eulerAngles.y, seed);
            }

            // Teleport presets
            if (Input.GetKeyDown(KeyCode.Alpha1)) Teleport(new Vector3(12f, 38f, -48f), 0f, 16f);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Teleport(new Vector3(40f, 60f, -10f), -42f, 22f);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Teleport(new Vector3(0f, -15f, 0f), 25f, 8f);

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

            if (streamer != null && Input.GetKeyDown(KeyCode.N))
            {
                streamer.ChangeSeed(streamer.worldSeed + 17);
                GroundedReady = false;
            }
        }

        public void Teleport(Vector3 pos, float yaw, float pitch)
        {
            _cc.enabled = false;
            transform.position = pos;
            _cc.enabled = true;
            _yaw = yaw;
            _pitch = pitch;
            _verticalVel = 0f;
            GroundedReady = false;
            streamer?.ForceSendPose();
        }

        private void EnsureSafeGroundSpawn()
        {
            if (GroundedReady) return;
            // Wait until the streamed LOD 0 chunk collider exists under the player, then gently snap onto it
            Vector3 probeOrigin = new Vector3(transform.position.x, transform.position.y + 40f, transform.position.z);
            if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, 140f))
            {
                _cc.enabled = false;
                transform.position = hit.point + Vector3.up * 0.25f;
                _cc.enabled = true;
                _verticalVel = 0f;
                GroundedReady = true;
            }
        }

        private void HandleCameraLook(float dt)
        {
            // Right-mouse drag orbits camera (Left-click swings greatsword when using rigged character!)
            if (Input.GetMouseButton(1) || (characterModelPrefab == null && Input.GetMouseButton(0)))
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
            {
                cameraDistance = Mathf.Clamp(cameraDistance - scroll * 0.7f, 2.5f, 16f);
            }
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
            Vector3 moveDir = (yawRot * new Vector3(inX, 0f, inZ));
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

            IsSwimming = transform.position.y < waterLevel - 0.65f;

            // Dodge Roll (C or LeftCtrl)
            if (!IsSwimming && _rollTimer <= 0f && _cc.isGrounded && CurrentStamina >= 22f &&
                (Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.LeftControl)))
            {
                _rollTimer = 0.36f;
                _rollDir = moveDir.sqrMagnitude > 0.01f ? moveDir : transform.forward;
                CurrentStamina = Mathf.Max(0f, CurrentStamina - 22f);
                if (_animator != null) _animator.SetTrigger("Roll");
            }

            bool wantsSprint = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.F)) && moveDir.sqrMagnitude > 0.01f && CurrentStamina > 2f;
            float speed;
            Vector3 horiz;

            if (_rollTimer > 0f)
            {
                _rollTimer -= dt;
                speed = sprintSpeed * 1.45f;
                horiz = _rollDir * speed;
            }
            else if (IsSwimming)
            {
                speed = wantsSprint ? swimSpeed * 1.4f : swimSpeed;
                horiz = moveDir * speed;
                if (wantsSprint) CurrentStamina = Mathf.Max(0f, CurrentStamina - 14f * dt);
                else CurrentStamina = Mathf.Min(MaxStamina, CurrentStamina + 14f * dt);
            }
            else
            {
                speed = wantsSprint ? sprintSpeed : walkSpeed;
                horiz = moveDir * speed;
                if (wantsSprint) CurrentStamina = Mathf.Max(0f, CurrentStamina - 18f * dt);
                else CurrentStamina = Mathf.Min(MaxStamina, CurrentStamina + 22f * dt);
            }

            // Vertical velocity (buoyancy when swimming, jump + gravity on land)
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
                if (_cc.isGrounded && _verticalVel < 0f)
                    _verticalVel = -2.0f;

                if (_cc.isGrounded && Input.GetKeyDown(KeyCode.Space) && CurrentStamina >= 10f)
                {
                    _verticalVel = jumpForce;
                    CurrentStamina = Mathf.Max(0f, CurrentStamina - 10f);
                    if (_animator != null) _animator.SetTrigger("Jump");
                }
                _verticalVel -= gravity * dt;
            }

            Vector3 motion = (horiz + Vector3.up * _verticalVel) * dt;
            _cc.Move(motion);
            _currentHorizSpeed = new Vector2(horiz.x, horiz.z).magnitude;

            // Smoothly rotate character toward movement direction + drive skeletal Animator or fallback
            if (horiz.sqrMagnitude > 0.05f)
            {
                Quaternion targetRot = Quaternion.LookRotation(new Vector3(horiz.x, 0f, horiz.z).normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 12f * dt);
                _walkCycle += dt * (wantsSprint ? 13f : 8.5f);
            }
            else
            {
                _walkCycle = Mathf.Lerp(_walkCycle, 0f, 8f * dt);
            }

            AnimateAvatar();
        }

        private void AnimateAvatar()
        {
            if (_animator != null)
            {
                _animator.SetFloat("Speed", _currentHorizSpeed);
                _animator.SetBool("IsGrounded", _cc.isGrounded);
                _animator.SetBool("IsSwimming", IsSwimming);
                return;
            }
            if (_avatarRoot == null) return;
            float swing = Mathf.Sin(_walkCycle) * 28f;
            if (_leftLeg != null) _leftLeg.localRotation = Quaternion.Euler(swing, 0f, 0f);
            if (_rightLeg != null) _rightLeg.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            float pitchTilt = _rollTimer > 0f ? 42f : (IsSwimming ? 24f : 0f);
            _avatarRoot.localRotation = Quaternion.Euler(pitchTilt, 0f, 0f);
        }

        private void SpawnRiggedCharacter()
        {
            var inst = Instantiate(characterModelPrefab, transform);
            inst.name = "RiggedHero";
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            _avatarRoot = inst.transform;
            _animator = inst.GetComponentInChildren<Animator>();

            // Attach toggleable HDRP torch light near shoulder/hand
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

            // Pull camera forward if terrain sits between pivot and camera
            if (Physics.SphereCast(pivot, 0.28f, desiredOffset.normalized, out RaycastHit hit, cameraDistance))
            {
                camPos = pivot + desiredOffset.normalized * Mathf.Max(1.0f, hit.distance - 0.15f);
            }

            playerCamera.transform.position = camPos;
            playerCamera.transform.rotation = camRot;
        }

        private void UpdateTorchVisuals()
        {
            if (!TorchLit || _torchLight == null) return;
            float flicker = 0.88f + 0.14f * Mathf.Sin(Time.time * 17.3f) + 0.08f * Mathf.Cos(Time.time * 29.1f);
            if (_hdTorch != null)
                _hdTorch.SetIntensity(650f * flicker, LightUnit.Lumen);
            else
                _torchLight.intensity = 650f * flicker;
        }

        private void ApplySunAngle()
        {
            if (sunLight == null) return;
            float t = Mathf.InverseLerp(4f, 80f, sunElevationDeg);
            sunLight.transform.rotation = Quaternion.Euler(sunElevationDeg, -38f + t * 72f, 0f);
            float kelvin = Mathf.Lerp(3200f, 5900f, Mathf.Pow(t, 0.55f));
            float lux = Mathf.Lerp(28000f, 115000f, Mathf.Pow(t, 0.5f));
            sunLight.useColorTemperature = true;
            sunLight.colorTemperature = kelvin;
            if (_hdSun != null) _hdSun.SetIntensity(lux, LightUnit.Lux);
            else sunLight.intensity = lux;
        }

        private void BuildStylizedAdventurerAvatar()
        {
            var lit = Shader.Find("HDRP/Lit");
            if (lit == null) return;

            _avatarRoot = new GameObject("AdventurerAvatar").transform;
            _avatarRoot.SetParent(transform, false);

            Material MakeMat(Color col, float smooth, float metal, Color emissive)
            {
                var m = new Material(lit);
                m.SetColor("_BaseColor", col);
                m.SetFloat("_Smoothness", smooth);
                m.SetFloat("_Metallic", metal);
                if (emissive.maxColorComponent > 0.01f)
                {
                    m.SetColor("_EmissiveColor", emissive);
                    m.SetColor("_EmissiveColorLDR", emissive);
                    m.SetFloat("_UseEmissiveIntensity", 1f);
                    m.SetFloat("_EmissiveIntensity", 8f);
                }
                HDMaterial.ValidateMaterial(m);
                return m;
            }

            var armorMat = MakeMat(new Color(0.18f, 0.21f, 0.25f), 0.55f, 0.65f, Color.black);
            var cloakMat = MakeMat(new Color(0.38f, 0.11f, 0.10f), 0.15f, 0f, Color.black);
            var leatherMat = MakeMat(new Color(0.24f, 0.16f, 0.10f), 0.28f, 0f, Color.black);
            var steelMat = MakeMat(new Color(0.76f, 0.80f, 0.86f), 0.82f, 0.95f, Color.black);
            var flameMat = MakeMat(new Color(1.0f, 0.55f, 0.15f), 0.4f, 0f, new Color(4.5f, 1.8f, 0.35f));

            GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 lp, Vector3 ls, Vector3 rot, Material mat)
            {
                var g = GameObject.CreatePrimitive(type);
                g.name = name;
                Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);
                g.transform.localPosition = lp;
                g.transform.localScale = ls;
                g.transform.localEulerAngles = rot;
                g.GetComponent<MeshRenderer>().sharedMaterial = mat;
                return g;
            }

            // Torso & shoulders
            Part(PrimitiveType.Cube, "Torso", _avatarRoot, new Vector3(0f, 1.15f, 0f), new Vector3(0.50f, 0.62f, 0.28f), Vector3.zero, armorMat);
            Part(PrimitiveType.Sphere, "Head", _avatarRoot, new Vector3(0f, 1.66f, 0f), new Vector3(0.32f, 0.34f, 0.32f), Vector3.zero, leatherMat);
            // Cloak
            Part(PrimitiveType.Cube, "Cloak", _avatarRoot, new Vector3(0f, 0.98f, -0.17f), new Vector3(0.48f, 0.88f, 0.05f), new Vector3(8f, 0f, 0f), cloakMat);
            // Witcher-style Greatsword slung across back
            Part(PrimitiveType.Cube, "SwordBlade", _avatarRoot, new Vector3(0.05f, 1.25f, -0.22f), new Vector3(0.08f, 1.25f, 0.03f), new Vector3(0f, 0f, -24f), steelMat);
            Part(PrimitiveType.Cube, "SwordCrossguard", _avatarRoot, new Vector3(-0.15f, 1.68f, -0.22f), new Vector3(0.30f, 0.05f, 0.06f), new Vector3(0f, 0f, -24f), leatherMat);

            // Articulated Legs
            _leftLeg = new GameObject("LeftLegPivot").transform;
            _leftLeg.SetParent(_avatarRoot, false);
            _leftLeg.localPosition = new Vector3(-0.14f, 0.82f, 0f);
            Part(PrimitiveType.Cube, "LeftLeg", _leftLeg, new Vector3(0f, -0.40f, 0f), new Vector3(0.18f, 0.80f, 0.20f), Vector3.zero, leatherMat);

            _rightLeg = new GameObject("RightLegPivot").transform;
            _rightLeg.SetParent(_avatarRoot, false);
            _rightLeg.localPosition = new Vector3(0.14f, 0.82f, 0f);
            Part(PrimitiveType.Cube, "RightLeg", _rightLeg, new Vector3(0f, -0.40f, 0f), new Vector3(0.18f, 0.80f, 0.20f), Vector3.zero, leatherMat);

            // Handheld HDRP Torch (toggled with T)
            _torchObj = new GameObject("HandheldTorch").transform;
            _torchObj.SetParent(_avatarRoot, false);
            _torchObj.localPosition = new Vector3(0.36f, 1.32f, 0.34f);
            Part(PrimitiveType.Cylinder, "TorchHandle", _torchObj, new Vector3(0f, -0.18f, 0f), new Vector3(0.05f, 0.26f, 0.05f), new Vector3(16f, 0f, 0f), leatherMat);
            Part(PrimitiveType.Sphere, "TorchFlame", _torchObj, new Vector3(0f, 0.10f, 0.06f), new Vector3(0.16f, 0.22f, 0.16f), Vector3.zero, flameMat);

            var lightGo = new GameObject("TorchHDRPLight");
            lightGo.transform.SetParent(_torchObj, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.16f, 0.10f);
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
