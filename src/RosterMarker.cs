using System;
using UnityEngine;
using UnityEngine.UI;
using UI;

namespace RuinaNet
{
    /// <summary>
    /// Marks who drives whom on the 开启舞台 screen.
    ///
    /// The split is positional: the host takes the first HostCount entries of the
    /// floor's line-up and the peer takes the rest, and that is the same order the
    /// battle itself uses (StageController walks the floor's unit list and hands
    /// out IsAddedBattle in order).  So "which portraits are mine" is just the
    /// index against Room.HostCount -- this paints that onto the portraits, since
    /// the game's own panel has no idea a second player exists.
    ///
    /// Runs only on 开启舞台 and only while a link is up.
    /// </summary>
    internal static class RosterMarker
    {
        private const int MaxSlots = 12;
        private const float RefW = 1920f;
        private const float RefH = 1080f;

        private static GameObject _root;
        private static Text[] _labels;
        private static Image[] _pills;
        private static Font _font;
        private static bool _built;
        private static bool _buildFailed;
        private static bool _on;

        private static readonly Color MineColor = RuinaTheme.Paper;
        private static readonly Color PeerColor = RuinaTheme.Gold;

        public static void Tick(float dt)
        {
            bool want = Room.Linked && Room.OnCoopScreen() && !CreatureSync.Active;

            if (!want)
            {
                if (_on)
                {
                    _on = false;
                    if (_root != null) _root.SetActive(false);
                }
                return;
            }

            if (_buildFailed) return;

            if (!_built)
            {
                Build();
                if (!_built) return;
            }

            if (!_on)
            {
                _on = true;
                _root.SetActive(true);
            }

            try
            {
                Render();
            }
            catch (Exception e)
            {
                _buildFailed = true;
                Debug.LogError("[RuinaNet][roster] render failed: " + e);
            }
        }

        private static void Render()
        {
            int used = 0;

            UIBattleSettingPanel panel = GetPanel();
            if (panel != null && panel.currentAvailbleUnitslots != null)
            {
                for (int i = 0; i < panel.currentAvailbleUnitslots.Count && used < MaxSlots; i++)
                {
                    UICharacterSlot slot = panel.currentAvailbleUnitslots[i];
                    if (slot == null) continue;

                    RectTransform rect = slot.transform as RectTransform;
                    if (rect == null) continue;

                    // Sit the pill just above the portrait.
                    Vector3 world = rect.TransformPoint(new Vector3(0f, rect.rect.height * 0.5f + 16f, 0f));
                    Vector3 screen = RectTransformUtility.WorldToScreenPoint(null, world);

                    // Whoever ticked it owns it; a librarian nobody touched falls
                    // back to the positional split (host takes the first
                    // HostCount entries).
                    bool positional = NetLink.IsHost ? (i < Room.HostCount) : (i >= Room.HostCount);
                    bool mine = Lineup.OwnsUnitData(slot.unitBattleData, positional);
                    Place(used++, screen, mine ? "我" : PeerLabel(), mine ? MineColor : PeerColor);
                }
            }

            for (int i = used; i < MaxSlots; i++)
            {
                if (_labels[i] != null && _labels[i].gameObject.activeSelf) _labels[i].gameObject.SetActive(false);
            }
        }

        private static void Place(int index, Vector3 screen, string text, Color color)
        {
            Text label = _labels[index];
            if (label == null) return;

            float nx = Mathf.Clamp01(screen.x / Mathf.Max(1f, (float)Screen.width));
            float ny = Mathf.Clamp01(screen.y / Mathf.Max(1f, (float)Screen.height));
            Vector2 pos = new Vector2((nx - 0.5f) * RefW, (ny - 0.5f) * RefH);

            label.rectTransform.anchoredPosition = pos;
            if (label.text != text) label.text = text;
            if (label.color != color) label.color = color;

            Image pill = _pills[index];
            if (pill != null)
            {
                Color fill = new Color(color.r, color.g, color.b, 0.20f);
                if (pill.color != fill) pill.color = fill;
            }

            if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
        }

        private static string PeerLabel()
        {
            string n = NetLink.PeerName;
            if (string.IsNullOrEmpty(n)) return "对方";
            n = n.Trim();
            if (n.Length == 0) return "对方";
            if (n.Length > 10) n = n.Substring(0, 10) + "…";
            return n;
        }

        private static UIBattleSettingPanel GetPanel()
        {
            try
            {
                UI.UIController ui = UI.UIController.Instance;
                if (ui == null) return null;
                return ui.GetUIPanel(UIPanelType.BattleSetting) as UIBattleSettingPanel;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Build()
        {
            try
            {
                _font = NetPanel.ResolveFont();

                GameObject canvasGo = new GameObject("RuinaNet_Roster");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);

                Canvas canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // Above the chat window (32100) and the battle overlay (32200).
                canvas.sortingOrder = 32250;

                CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(RefW, RefH);
                scaler.matchWidthOrHeight = 0.5f;

                // Decoration only: a live raycaster here would eat the game's clicks
                // on the very portraits we are labelling.
                GraphicRaycaster raycaster = canvasGo.AddComponent<GraphicRaycaster>();
                raycaster.enabled = false;

                _labels = new Text[MaxSlots];
                _pills = new Image[MaxSlots];

                for (int i = 0; i < MaxSlots; i++)
                {
                    GameObject go = new GameObject("Owner_" + i, typeof(Image));
                    go.transform.SetParent(canvasGo.transform, false);
                    go.SetActive(false);

                    Image img = go.GetComponent<Image>();
                    img.raycastTarget = false;
                    img.color = new Color(1f, 1f, 1f, 0.2f);
                    _pills[i] = img;

                    RectTransform rect = go.GetComponent<RectTransform>();
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.sizeDelta = new Vector2(150f, 26f);
                    rect.anchoredPosition = Vector2.zero;

                    GameObject textGo = new GameObject("Text", typeof(Text));
                    textGo.transform.SetParent(go.transform, false);
                    Text t = textGo.GetComponent<Text>();
                    t.font = _font;
                    t.fontSize = 17;
                    t.color = Color.white;
                    t.alignment = TextAnchor.MiddleCenter;
                    t.raycastTarget = false;
                    t.supportRichText = false;

                    Outline outline = textGo.AddComponent<Outline>();
                    outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                    outline.effectDistance = new Vector2(1f, -1f);

                    RectTransform textRect = textGo.GetComponent<RectTransform>();
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = Vector2.zero;
                    textRect.offsetMax = Vector2.zero;

                    _labels[i] = t;
                }

                _root = canvasGo;
                canvasGo.SetActive(false);
                _built = true;
                Debug.Log("[RuinaNet][roster] built (" + MaxSlots + " owner marks)");
            }
            catch (Exception e)
            {
                _buildFailed = true;
                _root = null;
                Debug.LogError("[RuinaNet][roster] build failed: " + e);
            }
        }
    }
}
