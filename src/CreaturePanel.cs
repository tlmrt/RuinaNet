using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace RuinaNet
{
    internal static class CreaturePanel
    {
        private static GameObject _root;
        private static Text _title, _status, _readyText;
        private static Font _font;
        private static Button _accept, _ready, _start, _config, _cancel;
        private static readonly Button[] Rows = new Button[5];
        private static readonly Text[] Labels = new Text[5];

        public static void Hide() { if (_root != null) _root.SetActive(false); }
        public static void Refresh()
        {
            if (!CreatureSync.ShowPanel) { Hide(); return; }
            if (_root == null) Build();
            _root.SetActive(true);
            bool offer = CreatureSync.Offered;
            _title.text = CreatureSync.Title;
            _status.text = CreatureSync.Status;
            _accept.gameObject.SetActive(offer);
            _ready.gameObject.SetActive(!offer);
            _start.gameObject.SetActive(!offer && BattleSync.IsHost);
            _config.gameObject.SetActive(!offer);
            _cancel.gameObject.SetActive(!CreatureSync.Locked);
            _ready.interactable = CreatureSync.Preparing && CreatureSync.CanExchange;
            _start.interactable = CreatureSync.CanStart;
            _config.interactable = CreatureSync.Preparing && CreatureSync.CanExchange;
            _readyText.text = CreatureSync.LocalReady ? "取消确认" : "确认配装";
            List<UnitBattleDataModel> units = offer ? new List<UnitBattleDataModel>() : CreatureSync.Units();
            for (int slot = 0; slot < 5; slot++)
            {
                bool show = !offer && slot < units.Count && (CreatureSync.Eligible & (1 << slot)) != 0;
                Rows[slot].gameObject.SetActive(show);
                if (!show) continue;
                UnitBattleDataModel data = units[slot];
                UnitDataModel unit = data.unitData;
                bool host = (CreatureSync.Hosts & (1 << slot)) != 0;
                string owner = !CreatureSync.CanExchange ? "等待分工" : (host == BattleSync.IsHost ? "你控制" : "对方控制");
                Labels[slot].text = (slot + 1) + "   " + unit.name +
                    (CreatureSync.IsPatron(slot) ? "（专属司书）" : "") + "     HP " + data.hp + "/" + unit.MaxHp +
                    "     核心书页 " + unit.bookItem.BookId.id + "     " + owner;
                Rows[slot].interactable = CreatureSync.Preparing && CreatureSync.CanExchange &&
                    BattleSync.IsHost && !CreatureSync.IsPatron(slot);
            }
        }
        private static void Build()
        {
            _font = NetPanel.ResolveFont();
            _root = new GameObject("RuinaNet_CreatureRoom", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32290;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            GameObject shade = new GameObject("Shade", typeof(Image)); shade.transform.SetParent(_root.transform, false);
            shade.GetComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            RectTransform sr = shade.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one; sr.offsetMin = sr.offsetMax = Vector2.zero;
            GameObject paper = Box(_root.transform, "CreatureRoom", Vector2.zero, new Vector2(980, 630));
            RuinaTheme.Frame(paper, true);
            _title = Label(paper.transform, "异想体联机", new Vector2(0, 262), new Vector2(920, 48), 27);
            _title.color = RuinaTheme.Gold;
            Label(paper.transform, "按原版名单上场 · 房主点击助理司书可切换控制方", new Vector2(0, 215), new Vector2(910, 35), 19);
            for (int slot = 0; slot < 5; slot++)
            {
                int index = slot;
                Rows[slot] = Button(paper.transform, "", new Vector2(0, 155 - slot * 53), new Vector2(900, 45),
                    delegate { CreatureSync.Assign(index); }, out Labels[slot]);
                Labels[slot].fontSize = 19;
            }
            _status = Label(paper.transform, "", new Vector2(0, -150), new Vector2(900, 85), 20);
            Text unused;
            _config = Button(paper.transform, "配置司书", new Vector2(-330, -256), new Vector2(190, 46), CreatureSync.Configure, out unused);
            _ready = Button(paper.transform, "确认配装", new Vector2(-110, -256), new Vector2(190, 46), CreatureSync.ToggleReady, out _readyText);
            _start = Button(paper.transform, "开始挑战", new Vector2(110, -256), new Vector2(190, 46), CreatureSync.Start, out unused);
            _cancel = Button(paper.transform, "取消 / 拒绝", new Vector2(330, -256), new Vector2(190, 46), CreatureSync.Cancel, out unused);
            _accept = Button(paper.transform, "接受邀请", new Vector2(-110, -256), new Vector2(190, 46), CreatureSync.Accept, out unused);
        }
        private static GameObject Box(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(Image)); go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = RuinaTheme.Ink;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos; rect.sizeDelta = size; return go;
        }
        private static Text Label(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize)
        {
            GameObject go = new GameObject("Label", typeof(Text)); go.transform.SetParent(parent, false);
            Text label = go.GetComponent<Text>(); label.font = _font; label.text = text; label.fontSize = fontSize;
            label.color = RuinaTheme.Paper; label.alignment = TextAnchor.MiddleCenter; label.raycastTarget = false;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos; rect.sizeDelta = size; return label;
        }
        private static Button Button(Transform parent, string text, Vector2 pos, Vector2 size,
            UnityEngine.Events.UnityAction action, out Text label)
        {
            GameObject go = Box(parent, "Button", pos, size);
            Button button = go.AddComponent<Button>(); button.targetGraphic = go.GetComponent<Image>();
            RuinaTheme.Button(go); button.onClick.AddListener(action);
            label = Label(go.transform, text, Vector2.zero, size - new Vector2(12, 4), 21);
            return button;
        }
    }
}
