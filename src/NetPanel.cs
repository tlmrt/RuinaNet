// RuinaNet - Stage 1 UI panel.
//
// Two ways to connect, in one panel:
//   * Steam friend invite / room id, using the game's Steam relay API
//   * IP direct (plain UDP - works through a SakuraFrp UDP tunnel,
//     Radmin VPN, a LAN, or a manual port forward)
//
// The panel is built at runtime with plain uGUI so the mod ships no assets.

using System;
using System.Text;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.UI;
// Steamworks.Data also defines Color and Image types; pin the aliases so
// every unqualified name in this file is the UnityEngine one.
using Color = UnityEngine.Color;
using Image = UnityEngine.UI.Image;

namespace RuinaNet
{
    internal static class NetPanel
    {
        private const string HintEmptyAddress =
            "先填对方地址，格式 IP:端口 或 域名:端口（例如 xxx.natfrp.com:12345）。";

        private static GameObject _root;
        private static Text _roomText;
        private static Text _memberText;
        private static Text _linkText;
        private static Text _hintText;
        private static InputField _addressField;
        private static InputField _steamRoomField;
        private static InputField _nameField;
        private static InputField _roomField;
        private static Font _font;
        private static float _pollTimer;

        public static bool IsOpen { get { return _root != null && _root.activeSelf; } }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public static void Open()
        {
            if (_root == null)
            {
                Build();
            }
            _root.SetActive(true);

            Refresh();
        }

        public static void Close()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        // Called every frame while the panel exists.
        public static void Tick(float dt)
        {
            if (!IsOpen)
            {
                return;
            }
            _pollTimer += dt;
            if (_pollTimer < 0.4f)
            {
                return;
            }
            _pollTimer = 0f;
            Refresh();
        }

        public static void Refresh()
        {
            if (_root == null || !_root.activeSelf)
            {
                return;
            }

            string myName = NetLink.LocalName;
            if (string.IsNullOrEmpty(myName))
            {
                myName = "司书(未命名)";
            }

            string peerName;
            if (!string.IsNullOrEmpty(NetLink.PeerName))
            {
                peerName = NetLink.PeerName;
            }
            else if (NetLink.Running && NetLink.Linked)
            {
                peerName = "(握手中)";
            }
            else
            {
                peerName = "-";
            }

            string room = NetLink.IsHost ? NetLink.RoomName : NetLink.PeerRoomName;
            if (string.IsNullOrEmpty(room))
            {
                room = "(未命名)";
            }

            if (_roomText != null)
            {
                _roomText.text = "接待室  /  " + room;
            }
            if (_memberText != null)
            {
                _memberText.text = "同行司书  /  " + peerName;
            }
            if (_linkText != null)
            {
                string ping = NetLink.PingMs >= 0 ? (NetLink.PingMs + " ms") : "-";
                string role = NetLink.Running ? (NetLink.IsHost ? "主机" : "客户端") : "未启动";
                _linkText.text = NetLink.TransportName + " " + NetLink.Status + "    " + role + "    延迟 " + ping;
            }
            if (_steamText != null) _steamText.text = SteamLink.RoomStatus;
        }

        private static Text _steamText;
        public static void ShowSteamInvite(string room)
        {
            Open();
            _steamRoomField.text = room;
            SetHint("收到 Steam 好友邀请。确认结束当前接待并断开后，点击加入 Steam 房间。");
        }
        private static void OnSteamJoin() { SteamLink.JoinRoom(_steamRoomField == null ? "" : _steamRoomField.text); }
        private static void CopyGroup() { GUIUtility.systemCopyBuffer = "1130976144"; SetHint("已复制 QQ 群号 1130976144，可用于找队友与反馈问题。"); }

        // The two identity fields feed the handshake directly: whatever is in
        // them at the next HELLO / WELCOME is what the other side sees.
        private static void OnNameEdited(string value)
        {
            string v = value == null ? "" : value.Trim();
            if (v.Length > 24)
            {
                v = v.Substring(0, 24);
            }
            NetLink.LocalName = v;
            if (_nameField != null)
            {
                _nameField.text = v;
            }
            Refresh();
        }

        private static void OnRoomNameEdited(string value)
        {
            string v = value == null ? "" : value.Trim();
            if (v.Length > 32)
            {
                v = v.Substring(0, 32);
            }
            NetLink.RoomName = v;
            if (_roomField != null)
            {
                _roomField.text = v;
            }
            Refresh();
        }

        public static void SetHint(string text)
        {
            if (_hintText != null)
            {
                _hintText.text = text;
            }
        }

        // --------------------------------------------------------------
        // Actions
        // --------------------------------------------------------------
        private static void OnSteamCreateRoom()
        {
            SteamLink.CreateRoom();
            Refresh();
        }

        private static void OnSteamInvite()
        {
            SteamLink.Invite();
            Refresh();
        }

        private static void OnLinkHost()
        {
            int port;
            bool fromField = ParsePortFromField(out port);
            NetLink.Host(port);
            SetHint("本机 UDP " + port + " 已监听" + (fromField ? "（端口取自输入框）" : "（默认端口）") +
                    "。用 SakuraFrp 建一条 UDP " + port + " 隧道，把公网域名+端口发给对方。");
            Refresh();
        }

        private static void OnLinkJoin()
        {
            string raw = _addressField != null ? _addressField.text : null;
            if (raw == null || raw.Trim().Length == 0)
            {
                SetHint(HintEmptyAddress);
                return;
            }

            string host = raw.Trim();
            int port = NetLink.DefaultPort;

            int colon = host.LastIndexOf(':');
            if (colon > 0 && colon < host.Length - 1)
            {
                int parsed;
                if (int.TryParse(host.Substring(colon + 1), out parsed) && parsed > 0 && parsed < 65536)
                {
                    port = parsed;
                    host = host.Substring(0, colon);
                }
            }

            NetLink.Join(host, port);
            SetHint("正在连接 " + host + ":" + port + " … 连不上就检查：隧道是 UDP 协议、端口一致、防火墙放行。");
            Refresh();
        }

        private static void OnLinkStop()
        {
            NetLink.Stop();
            SetHint("已断开。");
            Refresh();
        }

        private static void OnShowLocalIp()
        {
            SetHint("本机地址 " + NetLink.LocalAddressList() + "  端口 " + NetLink.DefaultPort +
                    "（局域网或 Radmin 直接用这个；公网走 SakuraFrp 给的域名+端口）");
        }

        private static void OnLinkSendPing()
        {
            if (!NetLink.Running)
            {
                SetHint("先开房或先连接，再发测试消息。");
                return;
            }
            NetLink.Send("hello");
            SetHint("已向对端发出一条测试消息（看日志里的 peer message）。");
        }

        private static bool ParsePortFromField(out int port)
        {
            port = NetLink.DefaultPort;
            if (_addressField == null)
            {
                return false;
            }
            string raw = _addressField.text;
            if (raw == null)
            {
                return false;
            }
            raw = raw.Trim();
            int colon = raw.LastIndexOf(':');
            if (colon <= 0 || colon >= raw.Length - 1)
            {
                return false;
            }
            int parsed;
            if (int.TryParse(raw.Substring(colon + 1), out parsed) && parsed > 0 && parsed < 65536)
            {
                port = parsed;
                return true;
            }
            return false;
        }

        // --------------------------------------------------------------
        // Construction
        // --------------------------------------------------------------
        private static void Build()
        {
            _font = ResolveFont();

            GameObject canvasGo = new GameObject("RuinaNet_Canvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();
            canvasGo.AddComponent<RuinaNetPanelTick>();

            _root = canvasGo;

            float w = 700f;
            float h = 810f;
            GameObject box = new GameObject("ReceptionRoom", typeof(Image));
            box.transform.SetParent(canvasGo.transform, false);
            RectTransform boxRect = box.GetComponent<RectTransform>();
            boxRect.anchorMin = boxRect.anchorMax = boxRect.pivot = new Vector2(0.5f, 0.5f);
            boxRect.sizeDelta = new Vector2(w, h);
            boxRect.anchoredPosition = Vector2.zero;
            RuinaTheme.Frame(box, true);
            Transform p = box.transform;

            MakeLabel(p, "联 机 接 待", 32, RuinaTheme.Gold, new Vector2(0,350), new Vector2(600,48));
            MakeLabel(p, "R U I N A N E T   /   两位司书 · 同一场接待", 15, RuinaTheme.Muted,
                new Vector2(0,310), new Vector2(600,26));
            MakeLabel(p, "司书名", 18, RuinaTheme.Paper, new Vector2(-255,158), new Vector2(100,36));
            _nameField = MakeInput(p, "你的昵称", new Vector2(50,158), new Vector2(470,40));
            _nameField.text = NetLink.LocalName;
            _nameField.onEndEdit.AddListener(OnNameEdited);
            MakeLabel(p, "接待室", 18, RuinaTheme.Paper, new Vector2(-255,104), new Vector2(100,36));
            _roomField = MakeInput(p, "为接待室命名", new Vector2(50,104), new Vector2(470,40));
            _roomField.text = NetLink.RoomName;
            _roomField.onEndEdit.AddListener(OnRoomNameEdited);

            MakeLabel(p, "─────────  邀 请 同 行  ─────────", 17, RuinaTheme.Gold,
                new Vector2(0,49), new Vector2(600,30));
            _addressField = MakeInput(p, "对方 IP 或域名:端口", new Vector2(-65,5), new Vector2(450,42));
            MakeButton(p, "加入接待", new Vector2(235,5), new Vector2(130,42), OnLinkJoin);
            MakeButton(p, "创建接待室", new Vector2(-200,-53), new Vector2(185,42), OnLinkHost);
            MakeButton(p, "查看本机地址", new Vector2(0,-53), new Vector2(185,42), OnShowLocalIp);
            MakeButton(p, "断开连接", new Vector2(200,-53), new Vector2(185,42), OnLinkStop);
            _linkText = MakeLabel(p, "", 17, RuinaTheme.Gold, new Vector2(0,-102), new Vector2(620,42));
            _roomText = MakeLabel(p, "", 16, RuinaTheme.Paper, new Vector2(-150,-142), new Vector2(300,28));
            _memberText = MakeLabel(p, "", 16, RuinaTheme.Paper, new Vector2(150,-142), new Vector2(300,28));
            _hintText = MakeLabel(p, "一方创建接待室，另一方填写地址加入。跨网络联机需先配置 UDP 内网穿透。",
                16, RuinaTheme.Muted, new Vector2(0,-191), new Vector2(590,54));
            MakeLabel(p, "Steam 好友联机 · 实验功能", 18, RuinaTheme.Gold, new Vector2(0,264), new Vector2(600,28));
            MakeButton(p, "创建 Steam 房间", new Vector2(-155,220), new Vector2(280,38), OnSteamCreateRoom);
            MakeButton(p, "邀请 Steam 好友", new Vector2(155,220), new Vector2(280,38), OnSteamInvite);
            _steamRoomField = MakeInput(p, "Steam 房间编号", new Vector2(-65,-250), new Vector2(450,38));
            _steamRoomField.text = SteamLink.PendingInvite;
            MakeButton(p, "加入 Steam", new Vector2(235,-250), new Vector2(130,38), OnSteamJoin);
            _steamText = MakeLabel(p, "", 15, RuinaTheme.Muted, new Vector2(0,-292), new Vector2(620,30));
            MakeButton(p, "交流与反馈 QQ：1130976144（复制）", new Vector2(0,-335), new Vector2(570,32), CopyGroup);
            MakeButton(p, "返  回", new Vector2(0,-378), new Vector2(180,36), Close);

            Refresh();
        }

        internal static Font ResolveFont()
        {
            Font f = null;
            // Use the original game's source font when its TMP asset retains one.
            try
            {
                TMPro.TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
                foreach (TMPro.TMP_FontAsset asset in fonts)
                {
                    System.Reflection.PropertyInfo prop = asset.GetType().GetProperty("sourceFontFile");
                    Font source = prop == null ? null : prop.GetValue(asset, null) as Font;
                    if (source != null && source.HasCharacter('图')) return source;
                }
            }
            catch (Exception) { }
            string[] preferred = { "SimSun", "Noto Serif CJK SC", "Source Han Serif SC", "Microsoft YaHei" };
            for (int i = 0; i < preferred.Length && f == null; i++)
            {
                try
                {
                    f = Font.CreateDynamicFontFromOSFont(preferred[i], 22);
                }
                catch (Exception)
                {
                    f = null;
                }
            }
            if (f == null)
            {
                try
                {
                    string[] all = Font.GetOSInstalledFontNames();
                    if (all != null && all.Length > 0)
                    {
                        f = Font.CreateDynamicFontFromOSFont(all[0], 22);
                    }
                }
                catch (Exception)
                {
                    f = null;
                }
            }
            return f;
        }

        private static Text MakeLabel(Transform parent, string content, int size, Color color, Vector2 pos, Vector2 dim)
        {
            GameObject go = new GameObject("Label", typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.text = content;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;
            return text;
        }

        private static void MakeButton(Transform parent, string label, Vector2 pos, Vector2 dim,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("Btn_" + label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.22f, 0.22f, 0.25f, 1f);

            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            RuinaTheme.Button(go);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;

            Text labelText = MakeLabel(go.transform, label, 19, RuinaTheme.Paper, Vector2.zero, dim);
            RectTransform labelRect = labelText.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelRect.anchoredPosition = Vector2.zero;
        }

        private static InputField MakeInput(Transform parent, string placeholderText, Vector2 pos, Vector2 dim)
        {
            GameObject go = new GameObject("Input", typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);

            Image image = go.GetComponent<Image>();
            image.color = new Color(0.14f, 0.14f, 0.16f, 1f);

            Text text = MakeLabel(go.transform, "", 18, RuinaTheme.Paper, Vector2.zero, dim);
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            RectTransform textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 0f);
            textRect.offsetMax = new Vector2(-10f, 0f);
            textRect.anchoredPosition = Vector2.zero;

            Text ph = MakeLabel(go.transform, placeholderText, 17, RuinaTheme.Muted, Vector2.zero, dim);
            ph.alignment = TextAnchor.MiddleLeft;
            ph.supportRichText = false;
            RectTransform phRect = ph.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(10f, 0f);
            phRect.offsetMax = new Vector2(-10f, 0f);
            phRect.anchoredPosition = Vector2.zero;

            InputField field = go.GetComponent<InputField>();
            field.targetGraphic = image;
            field.textComponent = text;
            field.placeholder = ph;
            field.text = "";
            field.characterLimit = 96;
            field.lineType = InputField.LineType.SingleLine;
            RuinaTheme.Input(go);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = dim;
            rect.anchoredPosition = pos;

            return field;
        }
    }

    // Drives both the panel refresh and the network heartbeat.
    public class RuinaNetPanelTick : MonoBehaviour
    {
        private void Update()
        {
            // The heartbeat must run whether or not the panel is on screen,
            // otherwise the link would time out the moment the player closes it.

            NetPanel.Tick(Time.unscaledDeltaTime);

            // Idempotent: drops the co-op button onto the team-setting panel
            // the first frame that panel becomes the active UI phase.


            // The chat window follows the link: it appears on its own once the
            // link is up and hides when it drops.


            // A live link is what arms the battle sync: every battle patch
            // checks BattleSync.Active first, so if nobody ever calls SetLink
            // the gate never intercepts and co-op quietly does nothing.
        }
    }

}
