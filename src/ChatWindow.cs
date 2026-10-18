// RuinaNet - in-room chat window.
//
// A small window that pops up on its own once the link comes up and hides
// again when it drops, so the player does not have to hunt for it. Separate
// from the connection panel on purpose: the panel is for setting a link up,
// chat is for using it.
//
// Protocol: CHAT rides inside the normal MSG envelope, so the line is
// "RUINANET1 MSG CHAT <text>". Anything after the command word is the body
// verbatim, spaces included.
//
// Compiles with the in-box C# 5 compiler.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RuinaNet
{
    internal static class ChatWindow
    {
        private const int MaxLines = 8;
        private const float Width = 460f;
        private const float Height = 470f;
        private const float TitleH = 36f;   // matches the title bar's own height below
        // The co-op strip hangs from the top of the body, so it stays put no
        // matter what height the window was saved with.
        private const float BarH = 66f;

        private static GameObject _root;
        private static GameObject _box;
        private static GameObject _body;
        private static RectTransform _boxRect;
        private static Text _title;
        private static Text _collapseLabel;
        private static Text _log;
        private static InputField _field;
        private static Font _font;
        private static bool _visible;
        private static bool _collapsed;
        private static bool _needsClamp;
        private static int _unread;

        // The co-op toolbar: book screen, back-to-reception, ready flag, split.
        private static GameObject _barRoot;
        private static RectTransform _coopRect;
        private static Text _bookLabel;
        private static Text _backLabel;
        private static Text _readyLabel;
        private static Text _hintLabel;
        private static InputField _mineField;
        private static InputField _peerField;
        private static Text _mineCaption;
        private static Text _peerCaption;
        private static bool _barBuilt;

        private static readonly List<string> _lines = new List<string>();

        // Called every frame from RuinaNetPanelTick.
        public static void Tick(float dt)
        {
            // Build() runs before the canvas has a size, so the first clamp is
            // re-done here, one frame later, when the rect is real.
            if (_needsClamp && _visible && _boxRect != null)
            {
                _needsClamp = false;
                _boxRect.anchoredPosition = ClampPos(_boxRect.anchoredPosition);
            }

            bool want = NetLink.Running && NetLink.Linked;
            if (want == _visible)
            {
                return;
            }
            _visible = want;
            if (want)
            {
                Show();
            }
            else
            {
                Hide();
            }
        }

        // ---------------- collapse / drag ----------------

        /// <summary>Fold the window down to just its title bar, or back open.</summary>
        public static void ToggleCollapse()
        {
            ApplyCollapsed(!_collapsed, true);
        }

        public static void BeginDrag()
        {
        }

        public static void DragBy(PointerEventData e)
        {
            if (_boxRect == null || e == null)
            {
                return;
            }
            // e.delta is in screen pixels; anchoredPosition is in canvas units.
            float scale = CanvasScale();
            _boxRect.anchoredPosition = ClampPos(_boxRect.anchoredPosition + e.delta / scale);
        }

        public static void EndDrag()
        {
            if (_boxRect == null)
            {
                return;
            }
            ModIni.SetFloat("chat", "pos_x", _boxRect.anchoredPosition.x);
            ModIni.SetFloat("chat", "pos_y", _boxRect.anchoredPosition.y);
        }

        private static float CanvasScale()
        {
            if (_root == null)
            {
                return 1f;
            }
            Canvas canvas = _root.GetComponent<Canvas>();
            if (canvas != null && canvas.scaleFactor > 0.01f)
            {
                return canvas.scaleFactor;
            }
            return 1f;
        }

        /// <summary>
        /// The box is anchored to the bottom-right corner with pivot (1,0), so
        /// x is how far left of that corner it sits and y how far up.  Keep the
        /// whole thing inside the canvas no matter what the ini says.
        /// </summary>
        private static Vector2 ClampPos(Vector2 pos)
        {
            float canvasW = 1920f;
            float canvasH = 1080f;
            if (_root != null)
            {
                RectTransform cr = _root.GetComponent<RectTransform>();
                if (cr != null && cr.rect.width > 200f && cr.rect.height > 200f)
                {
                    canvasW = cr.rect.width;
                    canvasH = cr.rect.height;
                }
                else
                {
                    // The canvas has not laid out yet (we are inside Build()).
                    // Leave the value alone; Tick re-clamps once it is live.
                    return pos;
                }
            }
            float h = _collapsed ? TitleH : Height;
            float minX = Mathf.Min(0f, Width - canvasW);
            float maxY = Mathf.Max(0f, canvasH - h);
            pos.x = Mathf.Clamp(pos.x, minX, 0f);
            pos.y = Mathf.Clamp(pos.y, 0f, maxY);
            return pos;
        }

        private static void ApplyCollapsed(bool collapsed, bool save)
        {
            _collapsed = collapsed;
            if (_body != null)
            {
                _body.SetActive(!collapsed);
            }
            if (_boxRect != null)
            {
                _boxRect.sizeDelta = new Vector2(Width, collapsed ? TitleH : Height);
                _boxRect.anchoredPosition = ClampPos(_boxRect.anchoredPosition);
            }
            if (_collapseLabel != null)
            {
                _collapseLabel.text = collapsed ? "展开" : "收起";
            }
            if (!collapsed)
            {
                _unread = 0;
            }
            UpdateTitle();
            if (save)
            {
                ModIni.SetBool("chat", "collapsed", collapsed);
            }
        }

        private static void UpdateTitle()
        {
            if (_title == null)
            {
                return;
            }
            string t = "房间聊天";
            if (_collapsed && _unread > 0)
            {
                t = t + "（" + _unread + " 条新消息）";
            }
            _title.text = t;
        }

        private static void ApplySavedLayout()
        {
            _collapsed = ModIni.GetBool("chat", "collapsed", false);
            float dx = ModIni.GetFloat("chat", "pos_x", -24f);
            float dy = ModIni.GetFloat("chat", "pos_y", 24f);
            _boxRect.sizeDelta = new Vector2(Width, _collapsed ? TitleH : Height);
            _boxRect.anchoredPosition = ClampPos(new Vector2(dx, dy));
            if (_body != null)
            {
                _body.SetActive(!_collapsed);
            }
            if (_collapseLabel != null)
            {
                _collapseLabel.text = _collapsed ? "展开" : "收起";
            }
            UpdateTitle();
        }

        public static void Show()
        {
            if (_root == null)
            {
                Build();
            }
            if (_root != null)
            {
                _root.SetActive(true);
                RefreshLog();
                SetStatus("已连接，说点什么。");
            }
        }

        public static void Hide()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        // Names ride along with the handshake, so both ends show the real one
        // instead of a faceless "me / the other guy".
        public static string SelfName()
        {
            return string.IsNullOrEmpty(NetLink.LocalName) ? "我" : NetLink.LocalName;
        }

        public static string PeerName()
        {
            return string.IsNullOrEmpty(NetLink.PeerName) ? "对方" : NetLink.PeerName;
        }

        /// <summary>Keep the co-op toolbar in step with the room state.</summary>
        public static void RefreshRoomBar()
        {
            if (!_barBuilt) return;
            try
            {
                // Only 开启舞台 -- nowhere else has anything to do with the room.
                // The book screen is the one exception: the player is stuck in it
                // (the game's own ESC sends them to the floor list), so the way
                // back has to live there too.
                bool onSetting = Room.OnCoopScreen();
                bool inBook = Room.InBookScreen();
                bool show = (onSetting || inBook) && (!CreatureSync.Active || (inBook && !CreatureSync.Locked));
                if (_barRoot != null && _barRoot.activeSelf != show) _barRoot.SetActive(show);
                if (!show) return;
                if (_backLabel != null) _backLabel.text = CreatureSync.Active ? "返回异想体准备" : "返回接待";

                if (_readyLabel != null)
                {
                    if (!Room.Linked) _readyLabel.text = "准备完成";
                    else if (Room.LocalReady) _readyLabel.text = "取消准备";
                    else if (Room.PeerReady) _readyLabel.text = "对方已准备";
                    else _readyLabel.text = "准备完成";
                }

                // The counts are no longer typed in -- each player ticks their own
                // librarians on the panel, so these two boxes are a live read-out
                // of how many each side ended up with (see Lineup).
                SyncCountField(_mineField, Room.MyCount);
                SyncCountField(_peerField, Room.OtherCount);

                if (_hintLabel != null)
                {
                    _hintLabel.text = !Room.Linked
                        ? "先连上对方"
                        : (Room.MaxCount > 0
                            ? ("各点自己的，最多 " + Room.MaxCount + " 人")
                            : "各点自己的司书");
                }

                // In the book screen only the way back makes sense.
                if (_bookLabel != null) _bookLabel.gameObject.SetActive(onSetting);
                if (_readyLabel != null) _readyLabel.gameObject.SetActive(onSetting);
                if (_hintLabel != null) _hintLabel.gameObject.SetActive(onSetting);
                if (_mineField != null) _mineField.gameObject.SetActive(onSetting);
                if (_peerField != null) _peerField.gameObject.SetActive(onSetting);
                if (_mineCaption != null) _mineCaption.gameObject.SetActive(onSetting);
                if (_peerCaption != null) _peerCaption.gameObject.SetActive(onSetting);
                if (_backLabel != null && _backLabel.rectTransform != null)
                {
                    // In the book screen it is the only button, so centre it.
                    _backLabel.rectTransform.anchoredPosition =
                        onSetting ? new Vector2(5f, 19f) : new Vector2(-115f, 19f);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void SyncCountField(InputField field, int value)
        {
            if (field == null) return;
            // Read-out only: the line-up is picked on the 开启舞台 panel now, so
            // typing here would be a second, contradicting source of truth.
            field.interactable = false;
            if (field.isFocused) return;
            string want = value.ToString();
            if (field.text != want) field.text = want;
        }

        // who: the speaker's display name; empty falls back so an early line
        // cannot show up as "[12:00] : hi".
        public static void AddLine(string who, string text)
        {
            if (string.IsNullOrEmpty(who))
            {
                who = "?";
            }
            if (text == null)
            {
                return;
            }
            text = text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 160)
            {
                text = text.Substring(0, 160) + "…";
            }

            string stamp = DateTime.Now.ToString("HH:mm");
            _lines.Add("[" + stamp + "] " + who + ": " + text);
            while (_lines.Count > MaxLines)
            {
                _lines.RemoveAt(0);
            }
            if (_collapsed)
            {
                _unread++;
                UpdateTitle();
            }
            RefreshLog();
            Debug.Log("[RuinaNet][chat] " + who + ": " + text);
        }

        private static void RefreshLog()
        {
            if (_log == null)
            {
                return;
            }
            if (_lines.Count == 0)
            {
                _log.text = "（还没有消息）";
                return;
            }
            _log.text = string.Join("\n", _lines.ToArray());
        }

        private static void Send()
        {
            if (_field == null)
            {
                return;
            }
            string text = _field.text;
            if (text == null)
            {
                return;
            }
            text = text.Trim();
            if (text.Length == 0)
            {
                return;
            }
            if (!NetLink.Running || !NetLink.Linked)
            {
                SetStatus("还没连上对方，发不出去。");
                return;
            }

            NetLink.Send("CHAT " + text);
            _field.text = "";
            AddLine(SelfName(), text);
        }

        private static void SetStatus(string text)
        {
            // The status shares the log area's placeholder behaviour: only
            // shown while the log is empty, so it never eats history.
            if (_log != null && _lines.Count == 0)
            {
                _log.text = text;
            }
        }

        // --------------------------------------------------------------
        // Construction (plain uGUI, same approach as NetPanel)
        // --------------------------------------------------------------
        private static void Build()
        {
            _font = NetPanel.ResolveFont();

            GameObject canvasGo = new GameObject("RuinaNet_ChatCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32100;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();
            _root = canvasGo;

            GameObject box = new GameObject("ChatBox", typeof(Image));
            box.transform.SetParent(canvasGo.transform, false);
            Image bg = box.GetComponent<Image>();
            bg.color = RuinaTheme.Ink;
            RuinaTheme.Frame(box, true);

            _box = box;
            RectTransform boxRect = box.GetComponent<RectTransform>();
            boxRect.anchorMin = new Vector2(1f, 0f);
            boxRect.anchorMax = new Vector2(1f, 0f);
            boxRect.pivot = new Vector2(1f, 0f);
            boxRect.sizeDelta = new Vector2(Width, Height);
            boxRect.anchoredPosition = new Vector2(-24f, 24f);
            _boxRect = boxRect;

            // ---- title bar: survives collapsing, doubles as the drag handle ----
            GameObject bar = new GameObject("TitleBar", typeof(Image));
            bar.transform.SetParent(box.transform, false);
            Image barImg = bar.GetComponent<Image>();
            barImg.color = RuinaTheme.Field;
            RectTransform barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0f, 1f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(0.5f, 1f);
            barRect.sizeDelta = new Vector2(0f, TitleH);
            barRect.anchoredPosition = Vector2.zero;
            bar.AddComponent<ChatDragHandle>();

            _title = MakeLabel(bar.transform, "房间聊天", 18, RuinaTheme.Gold,
                new Vector2(-14f, 0f), new Vector2(Width - 96f, TitleH - 8f));
            _title.alignment = TextAnchor.MiddleLeft;

            _collapseLabel = MakeButton(bar.transform, "收起",
                new Vector2(Width / 2f - 28f, 0f), new Vector2(48f, TitleH - 8f),
                ToggleCollapse);

            // ---- body: everything that disappears when collapsed ----
            GameObject body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(box.transform, false);
            RectTransform bodyRect = body.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.pivot = new Vector2(0.5f, 0.5f);
            bodyRect.offsetMin = Vector2.zero;
            bodyRect.offsetMax = Vector2.zero;
            bodyRect.anchoredPosition = Vector2.zero;
            _body = body;

            Transform p = body.transform;

            _log = MakeLabel(p, "（还没有消息）", 17, RuinaTheme.Paper,
                new Vector2(0f, -16f), new Vector2(Width - 24f, 272f));
            _log.alignment = TextAnchor.LowerLeft;
            _log.horizontalOverflow = HorizontalWrapMode.Wrap;
            _log.verticalOverflow = VerticalWrapMode.Truncate;
            _log.supportRichText = false;

            _field = MakeInput(p, "打字后回车发送", new Vector2(-40f, -180f),
                new Vector2(Width - 130f, 40f));

            MakeButton(p, "发送", new Vector2(Width / 2f - 52f, -180f),
                new Vector2(80f, 40f), Send);

            // ---- co-op toolbar ----
            // Only shown on 开启舞台 (UIPhase.BattleSetting): that is the screen
            // where the split, the ready flag and the book screen all make sense,
            // and it keeps the book button out of every other screen (it used to
            // be clickable from the invitation list, which just confused people).
            GameObject barRoot = new GameObject("CoopBar", typeof(RectTransform));
            barRoot.transform.SetParent(body.transform, false);
            RectTransform coopRect = barRoot.GetComponent<RectTransform>();
            // The body covers the whole window, title bar included, so the strip has
            // to start *below* the title bar -- hanging it off the body's top edge is
            // what put the buttons on top of "房间聊天".
            coopRect.anchorMin = new Vector2(0f, 1f);
            coopRect.anchorMax = new Vector2(1f, 1f);
            coopRect.pivot = new Vector2(0.5f, 1f);
            coopRect.sizeDelta = new Vector2(0f, BarH);
            coopRect.anchoredPosition = new Vector2(0f, -(TitleH + 6f));
            _coopRect = coopRect;
            _barRoot = barRoot;
            Transform bp = barRoot.transform;

            // row A sits 14px under the strip's top edge (the strip's centre is 33)
            _bookLabel = MakeButton(bp, "配置司书", new Vector2(-115f, 19f),
                new Vector2(120f, 28f), Room.OpenLibrarianConfig);
            _bookLabel.fontSize = 16;

            _backLabel = MakeButton(bp, "返回接待", new Vector2(5f, 19f),
                new Vector2(110f, 28f), Room.ReturnToReception);
            _backLabel.fontSize = 16;

            _readyLabel = MakeButton(bp, "准备完成", new Vector2(120f, 19f),
                new Vector2(110f, 28f), Room.ToggleReady);
            _readyLabel.fontSize = 16;

            _mineCaption = MakeLabel(bp, "我方", 16, RuinaTheme.Paper,
                new Vector2(-160f, -17f), new Vector2(56f, 28f));
            _mineCaption.alignment = TextAnchor.MiddleRight;
            _mineField = MakeNumberInput(bp, new Vector2(-98f, -17f), true);

            _peerCaption = MakeLabel(bp, "对方", 16, RuinaTheme.Paper,
                new Vector2(-4f, -17f), new Vector2(56f, 28f));
            _peerCaption.alignment = TextAnchor.MiddleRight;
            _peerField = MakeNumberInput(bp, new Vector2(58f, -17f), false);

            _hintLabel = MakeLabel(bp, "", 15, RuinaTheme.Muted,
                new Vector2(160f, -17f), new Vector2(140f, 28f));
            _hintLabel.alignment = TextAnchor.MiddleLeft;

            _barBuilt = true;

            _root.SetActive(false);
            ApplySavedLayout();
            RefreshLog();
            _needsClamp = true;
        }

        /// <summary>
        /// A two-digit read-out box for the librarian split.  It is not editable:
        /// the line-up is picked on the 开启舞台 panel (Lineup mirrors both sides'
        /// picks), so a typed number here would be a second, contradicting source
        /// of truth.  Its Enter handler is dropped for the same reason -- MakeInput
        /// funnels onEndEdit into Send(), which would post "3" into the room chat.
        /// </summary>
        private static InputField MakeNumberInput(Transform parent, Vector2 pos, bool mine)
        {
            InputField field = MakeInput(parent, "", pos, new Vector2(56f, 30f));
            field.onEndEdit.RemoveAllListeners();
            field.contentType = InputField.ContentType.IntegerNumber;
            field.characterLimit = 2;
            field.textComponent.fontSize = 17;
            field.textComponent.alignment = TextAnchor.MiddleCenter;
            field.interactable = false;
            ApplyReadOnlyLook(field);
            return field;
        }

        /// <summary>Dim a read-out box so it does not look like a text box.</summary>
        private static void ApplyReadOnlyLook(InputField field)
        {
            try
            {
                if (field.textComponent != null)
                {
                    field.textComponent.color = RuinaTheme.Paper;
                }
                if (field.placeholder != null)
                {
                    Graphic g = field.placeholder as Graphic;
                    if (g != null) g.enabled = false;
                }
            }
            catch (Exception) { }
        }

        private static Text MakeLabel(Transform parent, string value, int size, Color color,
            Vector2 pos, Vector2 dim)
        {
            GameObject go = new GameObject("Label", typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;

            return text;
        }

        private static Text MakeButton(Transform parent, string caption, Vector2 pos,
            Vector2 dim, UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("Button", typeof(Image));
            go.transform.SetParent(parent, false);

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.20f, 0.21f, 0.26f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
            RuinaTheme.Button(go);

            GameObject labelGo = new GameObject("Label", typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            Text label = labelGo.GetComponent<Text>();
            label.font = _font;
            label.text = caption;
            label.fontSize = 18;
            label.color = RuinaTheme.Paper;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;

            RectTransform labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;

            return label;
        }

        private static InputField MakeInput(Transform parent, string hint, Vector2 pos, Vector2 dim)
        {
            GameObject go = new GameObject("Input", typeof(Image));
            go.transform.SetParent(parent, false);

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.14f, 0.14f, 0.17f, 1f);

            GameObject textGo = new GameObject("Text", typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            Text text = textGo.GetComponent<Text>();
            text.font = _font;
            text.fontSize = 17;
            text.color = RuinaTheme.Paper;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            RectTransform textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);

            GameObject phGo = new GameObject("Placeholder", typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            Text ph = phGo.GetComponent<Text>();
            ph.font = _font;
            ph.fontSize = 17;
            ph.color = RuinaTheme.Muted;
            ph.text = hint;
            ph.alignment = TextAnchor.MiddleLeft;
            ph.supportRichText = false;

            RectTransform phRect = phGo.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(8f, 0f);
            phRect.offsetMax = new Vector2(-8f, 0f);

            InputField field = go.GetComponent<InputField>();
            if (field == null)
            {
                field = go.AddComponent<InputField>();
            }
            field.targetGraphic = image;
            field.textComponent = text;
            field.placeholder = ph;
            field.text = "";
            field.characterLimit = 160;
            field.lineType = InputField.LineType.SingleLine;
            RuinaTheme.Input(go);

            // Enter (and losing focus) fires onEndEdit; Send() ignores empties.
            field.onEndEdit.AddListener(delegate { Send(); });

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;

            return field;
        }
    }

    /// <summary>
    /// Drag handler for the chat window's title bar.  Lives on the bar itself so
    /// the window can be dragged anywhere; the collapse button on the same bar
    /// still gets its click because a press with no movement never begins a drag.
    /// </summary>
    internal class ChatDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnBeginDrag(PointerEventData eventData)
        {
            ChatWindow.BeginDrag();
        }

        public void OnDrag(PointerEventData eventData)
        {
            ChatWindow.DragBy(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            ChatWindow.EndDrag();
        }
    }
}
