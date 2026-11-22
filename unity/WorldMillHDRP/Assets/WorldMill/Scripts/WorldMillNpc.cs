using UnityEngine;

namespace WorldMill
{
    /// <summary>
    /// Interactive rigged Quest-Giver NPC (Witcher 3 style) backed by local RPG save store.
    /// </summary>
    public sealed class WorldMillNpc : MonoBehaviour
    {
        public string npcName = "Hearth-Smith Torin";
        public string npcTitle = "Master Smith of Oakhaven";
        public string questId = "q_river_crossing";
        [TextArea(2, 5)]
        public string greeting = "Welcome to Oakhaven, traveler. Follow the dirt road west to the flooded river bridge and speak with Aldric.";
        public float interactRadius = 5.0f;

        public Transform playerTransform;
        public WorldMillLocalRpgStore rpgStore;

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
            if (rpgStore == null)
                rpgStore = FindFirstObjectByType<WorldMillLocalRpgStore>();

            if (playerTransform == null) return;

            Vector3 toPlayer = playerTransform.position - transform.position;
            float dist = toPlayer.magnitude;

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
                _dialogueOpen = !_dialogueOpen;
            else if (_dialogueOpen && (dist > interactRadius + 3f || Input.GetKeyDown(KeyCode.Escape)))
                _dialogueOpen = false;
        }

        private void EnsureStyles()
        {
            if (_panelBg != null) return;
            _panelBg = MakeTex(new Color(0.05f, 0.06f, 0.08f, 0.90f));
            _borderTex = MakeTex(new Color(0.83f, 0.66f, 0.33f, 0.65f));

            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.93f, 0.80f, 0.48f) }
            };
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.68f, 0.72f, 0.78f) }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.93f, 0.95f) }
            };
            _promptStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.94f, 0.84f, 0.56f) }
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
                float pw = 290f, ph = 30f;
                var r = new Rect((Screen.width - pw) * 0.5f, Screen.height * 0.72f, pw, ph);
                GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), _borderTex);
                GUI.DrawTexture(r, _panelBg);
                GUI.Label(r, $"[E] Talk to {npcName}", _promptStyle);
                return;
            }

            float w = Mathf.Min(640f, Screen.width - 80f);
            float h = 165f;
            var box = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 42f, w, h);
            GUI.DrawTexture(new Rect(box.x - 1, box.y - 1, box.width + 2, box.height + 2), _borderTex);
            GUI.DrawTexture(box, _panelBg);

            GUI.Label(new Rect(box.x + 20, box.y + 14, w - 40, 24), npcName, _nameStyle);
            GUI.Label(new Rect(box.x + 20, box.y + 36, w - 40, 18), npcTitle, _titleStyle);
            GUI.Label(new Rect(box.x + 20, box.y + 62, w - 40, 58), $"\"{greeting}\"", _bodyStyle);

            if (rpgStore != null && !string.IsNullOrEmpty(questId))
            {
                if (GUI.Button(new Rect(box.x + 20, box.y + h - 38, 160, 26), "Track quest"))
                {
                    rpgStore.SetActiveQuest(questId);
                    _dialogueOpen = false;
                }
            }
            if (GUI.Button(new Rect(box.x + w - 110, box.y + h - 38, 90, 26), "Close [E]"))
                _dialogueOpen = false;
        }
    }
}
