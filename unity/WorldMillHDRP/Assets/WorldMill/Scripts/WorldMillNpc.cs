using UnityEngine;
using WorldMill.UI;

namespace WorldMill
{
    /// <summary>
    /// Interactive rigged Quest-Giver NPC (Witcher 3 style) that faces the player when nearby
    /// and opens an atmospheric dialogue panel with quest tracking & lore.
    /// </summary>
    public sealed class WorldMillNpc : MonoBehaviour
    {
        public string npcName = "Hearth-Smith Torin";
        public string npcTitle = "Master Smith of Oakhaven";
        public string questId = "q_river_crossing";
        [TextArea(2, 5)]
        public string greeting = "Another wanderer braves the ash-winds. The river swallowed the old crossing stones—follow the torches down to Aldric by the bridge.";
        public float interactRadius = 5.0f;

        public Transform playerTransform;
        public WorldMillRpgClient rpgClient;

        private bool _dialogueOpen;
        private Texture2D _panelBg;
        private Texture2D _borderTex;
        private GUIStyle _nameStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _promptStyle;

        private void Update()
        {
            if (playerTransform == null)
            {
                var p = FindFirstObjectByType<WorldMillPlayerController>();
                if (p != null) playerTransform = p.transform;
            }
            if (rpgClient == null)
                rpgClient = FindFirstObjectByType<WorldMillRpgClient>();

            if (playerTransform == null) return;

            Vector3 toPlayer = playerTransform.position - transform.position;
            float dist = toPlayer.magnitude;

            // Smoothly turn to face the player when nearby
            if (dist < 11f)
            {
                Vector3 flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
                if (flat.sqrMagnitude > 0.01f)
                {
                    Quaternion look = Quaternion.LookRotation(flat.normalized, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, look, 5f * Time.deltaTime);
                }
            }

            if (dist <= interactRadius && Input.GetKeyDown(KeyCode.E))
            {
                _dialogueOpen = !_dialogueOpen;
            }
            else if (_dialogueOpen && (dist > interactRadius + 3f || Input.GetKeyDown(KeyCode.Escape)))
            {
                _dialogueOpen = false;
            }
        }

        private void EnsureStyles()
        {
            if (_panelBg != null) return;
            _panelBg = MakeTex(new Color(0.06f, 0.07f, 0.10f, 0.90f));
            _borderTex = MakeTex(new Color(0.83f, 0.66f, 0.33f, 0.75f));

            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.95f, 0.80f, 0.45f) }
            };
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.70f, 0.75f, 0.82f) }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.93f, 0.95f) }
            };
            _promptStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.96f, 0.84f, 0.50f) }
            };
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            if (playerTransform == null) return;
            float dist = Vector3.Distance(playerTransform.position, transform.position);
            if (dist > interactRadius) return;

            EnsureStyles();

            if (!_dialogueOpen)
            {
                float pw = 320f, ph = 34f;
                var r = new Rect((Screen.width - pw) * 0.5f, Screen.height * 0.68f, pw, ph);
                GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), _borderTex);
                GUI.DrawTexture(r, _panelBg);
                GUI.Label(r, $"[E] Talk to {npcName}", _promptStyle);
                return;
            }

            float w = Mathf.Min(680f, Screen.width - 80f);
            float h = 175f;
            var box = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 42f, w, h);
            GUI.DrawTexture(new Rect(box.x - 2, box.y - 2, box.width + 4, box.height + 4), _borderTex);
            GUI.DrawTexture(box, _panelBg);

            GUI.Label(new Rect(box.x + 20, box.y + 14, w - 40, 26), npcName, _nameStyle);
            GUI.Label(new Rect(box.x + 20, box.y + 38, w - 40, 20), npcTitle, _titleStyle);
            GUI.Label(new Rect(box.x + 20, box.y + 64, w - 40, 62), $"\"{greeting}\"", _bodyStyle);

            if (rpgClient != null && !string.IsNullOrEmpty(questId))
            {
                if (GUI.Button(new Rect(box.x + 20, box.y + h - 40, 170, 28), "Track Quest"))
                {
                    rpgClient.SetActiveQuest(questId);
                    rpgClient.ShowToast($"Tracking quest from {npcName}");
                    _dialogueOpen = false;
                }
            }
            if (GUI.Button(new Rect(box.x + w - 120, box.y + h - 40, 100, 28), "Close [E]"))
            {
                _dialogueOpen = false;
            }
        }
    }
}
