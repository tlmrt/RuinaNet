// RuinaNet - battle overlay.
//
// Two things the peer cannot see on their own screen:
//   1. where the other player's mouse is
//   2. which librarians the other player owns
//
// Both are drawn on one always-on-top canvas that only exists while a co-op
// battle is actually running. Positions travel normalised (0..1) so the two
// ends never have to agree on a resolution, and the canvas scale is fixed at
// 1920x1080 so the same numbers mean the same place on both screens.
//
// The overlay is pure decoration: its raycaster is off, so it never swallows a
// click that belonged to the game.
//
// Compiles with the in-box C# 5 compiler.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace RuinaNet
{
    internal static class BattleOverlay
    {
        private const float RefW = 1920f;
        private const float RefH = 1080f;
        private const float MouseInterval = 0.033f;  // smooth enough to read as a real pointer
        private const float TagUpOffset = 2.05f;     // world units above the unit origin
        private const int MaxTags = 8;

        private static GameObject _root;
        private static RectTransform _cursor;
        private static Text _cursorText;
        private static readonly List<Text> _tags = new List<Text>();
        private static Font _font;

        private static float _sendAccum;
        private static bool _peerMouseValid;
        private static Vector2 _peerMouse;
        private static Vector2 _cursorPosition;
        private static bool _cursorPositionValid;
        private static float _lastPeerMouseAt = -10f;

        /// <summary>Latest pointer position from the peer, normalised 0..1.</summary>
        public static void SetPeerMouse(float x, float y)
        {
            _peerMouse = new Vector2(x, y);
            _peerMouseValid = true;
            _lastPeerMouseAt = Time.unscaledTime;
        }

        public static void Tick(float dt)
        {
            // Built as soon as we are in a battle with a live link -- NOT once the
            // peer's handshake lands.  The local half of the overlay (canvas,
            // watching our own pointer) must never depend on the peer being
            // reachable, otherwise the host window looks completely dead.
            // The peer-only parts below check BattleSync.Active on their own.
            bool want = NetLink.Running && NetLink.Linked && BattleSync.InBattleLocal();
            if (!want)
            {
                if (_root != null && _root.activeSelf)
                {
                    _root.SetActive(false);
                }
                _peerMouseValid = false;
                _cursorPositionValid = false;
                return;
            }

            EnsureBuilt();
            if (_root == null)
            {
                return;
            }
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            SendLocalMouse(dt);
            RenderCursor();
            RenderUnitTags();
        }

        // --------------------------------------------------------------
        // outgoing: where our own pointer is
        // --------------------------------------------------------------
        private static void SendLocalMouse(float dt)
        {
            _sendAccum += dt;
            if (_sendAccum < MouseInterval)
            {
                return;
            }
            _sendAccum = 0f;

            float mx = Input.mousePosition.x / Mathf.Max(1f, (float)Screen.width);
            float my = Input.mousePosition.y / Mathf.Max(1f, (float)Screen.height);
            if (mx < 0f || mx > 1f || my < 0f || my > 1f)
            {
                return;   // pointer left the game window
            }
            NetLink.Send("MOUSE " +
                mx.ToString("0.0000", CultureInfo.InvariantCulture) + " " +
                my.ToString("0.0000", CultureInfo.InvariantCulture));
        }

        // --------------------------------------------------------------
        // drawing
        // --------------------------------------------------------------
        private static void RenderCursor()
        {
            if (_cursor == null)
            {
                return;
            }
            if (!_peerMouseValid || !BattleSync.Active || Time.unscaledTime - _lastPeerMouseAt > 1.5f)
            {
                if (_cursor.gameObject.activeSelf)
                {
                    _cursor.gameObject.SetActive(false);
                }
                return;
            }
            if (!_cursor.gameObject.activeSelf)
            {
                _cursor.gameObject.SetActive(true);
            }
            Vector2 target = Normalised(_peerMouse.x, _peerMouse.y);
            if (!_cursorPositionValid)
            {
                _cursorPosition = target;
                _cursorPositionValid = true;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-24f * Time.unscaledDeltaTime);
                _cursorPosition = Vector2.Lerp(_cursorPosition, target, blend);
            }
            _cursor.anchoredPosition = _cursorPosition;
            if (_cursorText != null)
            {
                _cursorText.text = PeerLabel();
            }
        }

        private static void RenderUnitTags()
        {
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return;
            }

            List<BattleUnitModel> all = AlivePlayers();
            int used = 0;
            for (int i = 0; i < all.Count && used < MaxTags; i++)
            {
                BattleUnitModel u = all[i];
                if (u == null || u.view == null)
                {
                    continue;
                }
                if (BattleSync.IsMine(u))
                {
                    continue;   // our own half needs no label
                }

                Vector3 sp;
                try
                {
                    sp = cam.WorldToScreenPoint(u.view.WorldPosition + new Vector3(0f, TagUpOffset, 0f));
                }
                catch (Exception)
                {
                    continue;
                }
                if (sp.z <= 0f)
                {
                    continue;   // behind the camera
                }

                float nx = sp.x / Mathf.Max(1f, (float)Screen.width);
                float ny = sp.y / Mathf.Max(1f, (float)Screen.height);

                Text tag = Tag(used);
                if (tag == null)
                {
                    break;
                }
                tag.rectTransform.anchoredPosition = Normalised(nx, ny);
                tag.text = PeerLabel();
                if (!tag.gameObject.activeSelf)
                {
                    tag.gameObject.SetActive(true);
                }
                used++;
            }

            for (int i = used; i < _tags.Count; i++)
            {
                Text t = _tags[i];
                if (t != null && t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(false);
                }
            }
        }

        private static Vector2 Normalised(float nx, float ny)
        {
            return new Vector2((nx - 0.5f) * RefW, (ny - 0.5f) * RefH);
        }

        private static string PeerLabel()
        {
            string n = NetLink.PeerName;
            if (string.IsNullOrEmpty(n))
            {
                return "对方";
            }
            n = n.Trim();
            if (n.Length == 0) return "对方";
            // Keep the tag readable above a character while still identifying the
            // actual person.  The full name remains visible in the room panel.
            if (n.Length > 12) n = n.Substring(0, 12) + "…";
            return n;
        }

        // --------------------------------------------------------------
        // scene queries
        // --------------------------------------------------------------
        private static bool InBattle()
        {
            return AlivePlayers().Count > 0;
        }

        private static List<BattleUnitModel> AlivePlayers()
        {
            List<BattleUnitModel> empty = new List<BattleUnitModel>();
            try
            {
                if (BattleObjectManager.instance == null)
                {
                    return empty;
                }
                List<BattleUnitModel> l = BattleObjectManager.instance.GetAliveList(Faction.Player);
                if (l == null)
                {
                    return empty;
                }
                return l;
            }
            catch (Exception)
            {
                return empty;
            }
        }

        // The battle camera is the one the unit sprites are drawn with; falling
        // back to Camera.main covers the case where the tag is not what we think.
        private static Camera ResolveCamera()
        {
            try
            {
                BattleCamManager mgr = SingletonBehavior<BattleCamManager>.Instance;
                if (mgr != null)
                {
                    Camera c = mgr.mainCam;
                    if (c != null)
                    {
                        return c;
                    }
                }
            }
            catch (Exception)
            {
            }
            try
            {
                return Camera.main;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool _buildFailed;

        // --------------------------------------------------------------
        // construction
        // --------------------------------------------------------------
        private static void EnsureBuilt()
        {
            if (_root != null || _buildFailed)
            {
                return;
            }
            try
            {
                Build();
            }
            catch (Exception e)
            {
                _buildFailed = true;
                _root = null;
                Debug.LogError("[RuinaNet][overlay] build failed: " + e);
            }
        }

        private static void Build()
        {
            _font = NetPanel.ResolveFont();
            if (_font == null)
            {
                Debug.LogWarning("[RuinaNet][overlay] no OS font, labels will be blank");
            }

            GameObject canvasGo = new GameObject("RuinaNet_Overlay");
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32200;

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.matchWidthOrHeight = 0.5f;

            // Decoration only: a live raycaster here would eat game clicks.
            GraphicRaycaster raycaster = canvasGo.AddComponent<GraphicRaycaster>();
            raycaster.enabled = false;

            _cursor = MakeCursor(canvasGo.transform);

            for (int i = 0; i < MaxTags; i++)
            {
                _tags.Add(MakeTag(canvasGo.transform));
            }

            _root = canvasGo;
            canvasGo.SetActive(false);
            Debug.Log("[RuinaNet][overlay] built (" + MaxTags + " unit tags + peer cursor)");
        }

        private static RectTransform MakeCursor(Transform parent)
        {
            GameObject go = new GameObject("PeerCursor", typeof(Image));
            go.transform.SetParent(parent, false);

            Image img = go.GetComponent<Image>();
            img.sprite = MakeCursorSprite();
            img.color = Color.white;
            img.preserveAspect = true;
            img.raycastTarget = false;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);      // the tip of the marker sits on the point
            rect.sizeDelta = new Vector2(26f, 34f);
            rect.anchoredPosition = Vector2.zero;

            GameObject labelGo = new GameObject("Label", typeof(Text));
            labelGo.transform.SetParent(go.transform, false);

            _cursorText = labelGo.GetComponent<Text>();
            _cursorText.font = _font;
            _cursorText.fontSize = 13;
            _cursorText.color = new Color(0.6f, 0.95f, 1f, 1f);
            _cursorText.alignment = TextAnchor.UpperLeft;
            _cursorText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _cursorText.verticalOverflow = VerticalWrapMode.Overflow;
            _cursorText.raycastTarget = false;
            _cursorText.supportRichText = false;

            Outline outline = labelGo.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            RectTransform labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.sizeDelta = new Vector2(220f, 22f);
            labelRect.anchoredPosition = new Vector2(17f, -17f);

            return rect;
        }

        private static Sprite MakeCursorSprite()
        {
            const int w = 24;
            const int h = 32;
            Texture2D tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            Vector2[] poly = {
                new Vector2(1f, 30f), new Vector2(2f, 3f), new Vector2(8f, 10f),
                new Vector2(12f, 2f), new Vector2(16f, 4f), new Vector2(12f, 12f),
                new Vector2(22f, 12f)
            };
            bool[,] inside = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) inside[x, y] = InPolygon(new Vector2(x + 0.5f, y + 0.5f), poly);
            Color fill = new Color(0.30f, 0.88f, 1f, 0.98f);
            Color edge = new Color(0.02f, 0.08f, 0.11f, 1f);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!inside[x, y]) { tex.SetPixel(x, y, Color.clear); continue; }
                    bool border = false;
                    for (int oy = -1; oy <= 1 && !border; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox, ny = y + oy;
                            if (nx < 0 || nx >= w || ny < 0 || ny >= h || !inside[nx, ny]) { border = true; break; }
                        }
                    tex.SetPixel(x, y, border ? edge : fill);
                }
            }
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.05f, 0.95f), 100f);
        }

        private static bool InPolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i], b = polygon[j];
                if (((a.y > point.y) != (b.y > point.y)) &&
                    point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static Text MakeTag(Transform parent)
        {
            GameObject go = new GameObject("PeerUnitTag", typeof(Text));
            go.transform.SetParent(parent, false);

            Text t = go.GetComponent<Text>();
            t.font = _font;
            t.fontSize = 17;
            t.color = new Color(0.6f, 0.95f, 1f, 0.95f);
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = false;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(220f, 24f);
            rect.anchoredPosition = Vector2.zero;

            go.SetActive(false);
            return t;
        }

        private static Text Tag(int i)
        {
            if (i < 0 || i >= _tags.Count)
            {
                return null;
            }
            return _tags[i];
        }
    }
}
