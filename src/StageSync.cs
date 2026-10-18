// RuinaNet - reception (stage) sync.
//
// Without this the two players each have to hunt down the same invitation by
// hand and walk into it separately.  With it, the side that picks a reception
// broadcasts the stage, and the other side only has to answer a small prompt:
// pressing "加入" drives that player's own UI into the very same stage, so the
// two of them end up in "开启舞台" together without comparing screenshots.
//
// The hook is UIController.PrepareBattle, the single place the game itself
// calls when the player confirms an invitation -- every entry path goes through
// it, including workshop invitations.
//
// Protocol (inside the normal MSG envelope):
//   STAGE <packageId|-> <stageId> <bookCsv|->
// where bookCsv is "packageId:id" entries joined with '|'.
//
// Compiles with the in-box C# 5 compiler.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace RuinaNet
{
    internal static class StageSync
    {
        private const float Width = 700f;
        private const float Height = 246f;

        private static readonly Color Gold = new Color(0.76f, 0.59f, 0.28f, 1f);
        private static readonly Color GoldDim = new Color(0.36f, 0.25f, 0.10f, 1f);
        private static readonly Color Ink = new Color(0.025f, 0.023f, 0.022f, 0.985f);
        private static readonly Color Ivory = new Color(0.88f, 0.84f, 0.75f, 1f);

        private static GameObject _root;
        private static Text _title;
        private static Text _detail;
        private static Font _font;
        private static bool _built;
        private static bool _showing;

        // the request currently on screen
        private static string _stagePkg = "";
        private static int _stageId = -1;
        private static string _bookCsv = "-";
        private static string _fromName = "";
        // floor (SephirahType) the opener is on, or -1 when it did not say.  The
        // floor decides the whole line-up, so it has to be right before the stage
        // model is built -- see FloorSync.
        private static int _stageFloor = -1;

        // Set while we drive our own PrepareBattle, so the request does not
        // bounce straight back at the side that asked us to come along.
        private static bool _suppressBroadcast;

        // ---------------- outgoing ----------------

        /// <summary>Tell the peer which reception we just walked into.</summary>
        public static void Broadcast(object stageObj, object bookListObj)
        {
            try
            {
                StageClassInfo stage = stageObj as StageClassInfo;
                if (stage == null) return;

                // A brand new reception resets the ready/go handshake.  This runs
                // even for our own suppressed call, so joining also clears the
                // flags left over from the previous reception.
                Room.OnNewReception(stage._id);

                if (_suppressBroadcast) return;
                if (!NetLink.Running || !NetLink.Linked) return;

                int floor = FloorSync.Local();
                string line = "STAGE " + Enc(stage.workshopID) + " " +
                              stage._id.ToString(CultureInfo.InvariantCulture) + " " +
                              BookCsv(bookListObj as List<DropBookXmlInfo>) + " " +
                              floor.ToString(CultureInfo.InvariantCulture);
                NetLink.Send(line);
                Debug.Log("[RuinaNet][stage] told the peer about stage " + stage.id +
                          " on floor " + FloorSync.Name(floor));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][stage] broadcast failed: " + e.Message);
            }
        }

        private static string BookCsv(List<DropBookXmlInfo> books)
        {
            if (books == null || books.Count == 0) return "-";
            string s = "";
            for (int i = 0; i < books.Count; i++)
            {
                DropBookXmlInfo b = books[i];
                if (b == null) continue;
                if (s.Length > 0) s += "|";
                s += Enc(b.workshopID) + ":" + b._id.ToString(CultureInfo.InvariantCulture);
            }
            return s.Length == 0 ? "-" : s;
        }

        // ---------------- incoming ----------------

        public static bool HandleMessage(string[] p)
        {
            if (CreatureSync.Active) return true;
            try
            {
                if (p.Length < 4) return false;
                _stagePkg = Dec(p[1]);
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _stageId);
                _bookCsv = p[3];
                // 5th field (optional): the floor the opener is on.  Remembering
                // it now means the join prompt can already be right about it.
                _stageFloor = -1;
                if (p.Length > 4)
                {
                    int fl;
                    if (int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out fl))
                    {
                        _stageFloor = fl;
                        FloorSync.Note(fl);
                    }
                }
                _fromName = NetLink.PeerName;
                if (_fromName.Length == 0) _fromName = "对方";

                Debug.Log("[RuinaNet][stage] peer opened reception " + _stagePkg + ":" + _stageId +
                          " (we are in ui phase " + PhaseName() + ")");

                if (InBattleNow())
                {
                    ChatWindow.AddLine(_fromName, "对方开了接待 #" + _stageId + "，但已经在战斗里了");
                    return true;
                }
                if (!CanJoin())
                {
                    // Somewhere the game cannot be driven from without breaking:
                    // say so in chat instead of calling into the UI blind.
                    ChatWindow.AddLine(_fromName, "对方开始了接待 #" + _stageId +
                                        "，但你不在邀请函界面（回到邀请函后再让对方开一次）");
                    return true;
                }
                _showing = true;
                EnsureBuilt();
                RefreshText();
                if (_root != null) _root.SetActive(true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][stage] handle failed: " + e.Message);
                return true;
            }
        }

        public static void Tick(float dt)
        {
            if (CreatureSync.Active) { Hide(); return; }
            if (!_showing) return;
            if (!NetLink.Running || !NetLink.Linked)
            {
                // the tunnel died under the prompt: nothing to join any more
                Hide();
            }
        }

        private static void Accept()
        {
            Hide();
            if (CreatureSync.Active) return;
            try
            {
                LorId wanted = new LorId(_stagePkg, _stageId);
                StageClassInfo stage = Singleton<StageClassInfoList>.Instance.GetData(wanted);
                if (stage == null)
                {
                    Debug.LogWarning("[RuinaNet][stage] this client does not know stage " + wanted);
                    ChatWindow.AddLine("系统", "你的存档里没有这场接待（" + _stagePkg + ":" + _stageId + "）");
                    return;
                }

                List<DropBookXmlInfo> books = new List<DropBookXmlInfo>();
                if (_bookCsv != "-" && _bookCsv.Length > 0)
                {
                    string[] parts = _bookCsv.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string[] kv = parts[i].Split(new char[] { ':' });
                        if (kv.Length != 2) continue;
                        int bid;
                        if (!int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out bid)) continue;
                        books.Add(Singleton<DropBookXmlList>.Instance.GetData(new LorId(Dec(kv[0]), bid)));
                    }
                }

                // Point ourselves at the opener's floor *before* opening the stage:
                // PrepareBattle copies UIController.CurrentSephirah straight into
                // StageController, and that value decides which librarians line up
                // and which passives (and so which opening buffs) come with them.
                // Getting this in afterwards would leave the line-up already built
                // from the wrong floor.
                _suppressBroadcast = true;
                if (_stageFloor >= 0) FloorSync.Adopt(_stageFloor, true);
                UI.UIController.Instance.PrepareBattle(stage, books);
                Debug.Log("[RuinaNet][stage] joined stage " + wanted + " with " + books.Count + " book(s)");
                // From here on this reception is a co-op one: the start gate and the
                // ready handshake switch on.
                Room.OnNewReception(_stageId);
                Room.SendJoined(_stageId);
                ChatWindow.AddLine("系统", "已加入对方的接待，配好书页后点「准备完成」");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][stage] join failed: " + e.Message);
            }
            finally
            {
                _suppressBroadcast = false;
            }
        }

        private static void Decline()
        {
            Hide();
            if (NetLink.Running && NetLink.Linked)
            {
                NetLink.Send("STAGENO " + _stageId.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static void NoteDeclined(string[] p)
        {
            int id = -1;
            if (p.Length > 1) int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            ChatWindow.AddLine(NetLink.PeerName.Length == 0 ? "对方" : NetLink.PeerName,
                "对方没有加入这场接待");
            Debug.Log("[RuinaNet][stage] peer declined stage " + id);
        }

        private static void Hide()
        {
            _showing = false;
            if (_root != null) _root.SetActive(false);
        }

        private static void RefreshText()
        {
            // StageClassInfo.stageName is the XML source string, not the current
            // language's display text.  On a Chinese install several base stages
            // therefore showed their Korean source name.  The numeric reception id
            // is stable and language neutral until we can use the game's own
            // localized invitation widget.
            string number = _stageId < 0 ? "未知" : _stageId.ToString(CultureInfo.InvariantCulture);
            if (_title != null) _title.text = "接待邀请";
            if (_detail != null)
            {
                _detail.text = _fromName + " 邀请你参加接待\n" +
                               "接待编号  " + number + "\n" +
                               "接受后将自动进入同一接待";
            }
        }

        // ---------------- helpers ----------------

        private static bool InBattleNow()
        {
            try
            {
                return BattleSync.AliveUnitCount() > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// PrepareBattle swaps the background screen into the battle-setting
        /// view, so it only makes sense from the invitation flow.  Anywhere else
        /// (title screen, already inside a library floor) the call would fight
        /// whatever screen is up.
        /// </summary>
        private static bool CanJoin()
        {
            string phase = PhaseName();
            return phase == "Invitation" || phase == "BattleSetting";
        }

        private static string PhaseName()
        {
            try
            {
                return UI.UIController.Instance.CurrentUIPhase.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Enc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            try
            {
                return Uri.EscapeDataString(s);
            }
            catch (Exception)
            {
                return "-";
            }
        }

        private static string Dec(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-") return "";
            try
            {
                return Uri.UnescapeDataString(s);
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ---------------- the prompt ----------------

        private static void EnsureBuilt()
        {
            if (_built) return;
            if (_root != null) return;
            try
            {
                _font = NetPanel.ResolveFont();

                GameObject canvasGo = new GameObject("RuinaNet_StageCanvas");
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);

                Canvas canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32300;

                CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);

                canvasGo.AddComponent<GraphicRaycaster>();
                _root = canvasGo;

                // The game's menus use thin brass frames over near-black paper.
                // Build the same hierarchy from plain UI images so it scales with
                // every resolution and does not depend on a workshop texture.
                GameObject shadow = MakeRect(canvasGo.transform, "Shadow",
                    new Color(0f, 0f, 0f, 0.72f), new Vector2(8f, -9f),
                    new Vector2(Width + 16f, Height + 16f));

                GameObject box = MakeRect(canvasGo.transform, "StageFrame", GoldDim,
                    Vector2.zero, new Vector2(Width + 6f, Height + 6f));

                RectTransform boxRect = box.GetComponent<RectTransform>();
                boxRect.anchoredPosition = new Vector2(0f, 142f);
                shadow.GetComponent<RectTransform>().anchoredPosition += new Vector2(0f, 142f);

                GameObject paper = MakeRect(box.transform, "Paper", Ink,
                    Vector2.zero, new Vector2(Width, Height));

                // A second inner rule and the short ornaments around the heading
                // keep the panel close to Ruina's invitation and book UI.
                MakeRect(paper.transform, "InnerRule", new Color(0.19f, 0.13f, 0.06f, 1f),
                    Vector2.zero, new Vector2(Width - 18f, Height - 18f));
                GameObject inner = MakeRect(paper.transform, "InnerPaper", new Color(0.04f, 0.035f, 0.032f, 1f),
                    Vector2.zero, new Vector2(Width - 22f, Height - 22f));

                MakeRect(inner.transform, "TitleLineL", GoldDim,
                    new Vector2(-222f, Height / 2f - 43f), new Vector2(142f, 2f));
                MakeRect(inner.transform, "TitleLineR", GoldDim,
                    new Vector2(222f, Height / 2f - 43f), new Vector2(142f, 2f));

                _title = MakeLabel(inner.transform, "接待邀请", 25, Gold,
                    new Vector2(0f, Height / 2f - 43f), new Vector2(250f, 36f));

                _detail = MakeLabel(inner.transform, "", 18, Ivory,
                    new Vector2(0f, 18f), new Vector2(Width - 70f, 92f));
                _detail.lineSpacing = 1.18f;

                MakeButton(inner.transform, "接受邀请", new Vector2(-108f, -Height / 2f + 43f),
                    new Vector2(190f, 44f), true, Accept);

                MakeButton(inner.transform, "暂不加入", new Vector2(108f, -Height / 2f + 43f),
                    new Vector2(190f, 44f), false, Decline);

                _root.SetActive(false);
                _built = true;
                Debug.Log("[RuinaNet][stage] prompt built");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][stage] prompt build failed: " + e.Message);
                _root = null;
            }
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

        private static GameObject MakeRect(Transform parent, string name, Color color,
            Vector2 pos, Vector2 dim)
        {
            GameObject go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;
            return go;
        }

        private static void MakeButton(Transform parent, string caption, Vector2 pos,
            Vector2 dim, bool primary, UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("Button", typeof(Image));
            go.transform.SetParent(parent, false);

            Image border = go.GetComponent<Image>();
            border.color = primary ? Gold : new Color(0.38f, 0.34f, 0.29f, 1f);

            GameObject fillGo = MakeRect(go.transform, "Fill",
                primary ? new Color(0.16f, 0.115f, 0.045f, 1f) : new Color(0.085f, 0.078f, 0.068f, 1f),
                Vector2.zero, new Vector2(dim.x - 4f, dim.y - 4f));
            Image fill = fillGo.GetComponent<Image>();
            fill.raycastTarget = false;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = border;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = primary ? new Color(1f, 0.90f, 0.58f, 1f) : new Color(0.82f, 0.76f, 0.66f, 1f);
            colors.pressedColor = new Color(0.72f, 0.56f, 0.28f, 1f);
            colors.disabledColor = new Color(0.25f, 0.24f, 0.22f, 0.65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);

            GameObject labelGo = new GameObject("Label", typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            Text label = labelGo.GetComponent<Text>();
            label.font = _font;
            label.text = caption;
            label.fontSize = 20;
            label.color = primary ? new Color(1f, 0.91f, 0.66f, 1f) : Ivory;
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
        }
    }
}
