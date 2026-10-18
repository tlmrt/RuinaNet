// RuinaNet - co-op entry button.
//
// WHERE IT LIVES: the invitation screen (UI.UIInvitationPanel) only, right
// next to the "邀请函" heading in the top-left corner. That is the screen where
// the player picks a reception and presses "send invitation", so it is the
// natural place to bring a friend in. The pre-battle team-setting screen is
// deliberately excluded: the user asked for the entry here and nowhere else,
// and a second copy there just cluttered the flow.
//
// WHY AN OWN CANVAS: the game panels toggle an ActiveControl child and run a
// reveal animation over it, so a button parented under the panel would either
// ride the animation or stay visible after the panel closed. A dedicated
// overlay canvas (sortingOrder above the game's UI) plus a per-frame phase
// check is exact and cannot desync.
//
// WHY NO HARMONY PATCH: OnOpen/Initialize are virtual overrides, and patching
// an override from a mod assembly is fragile. A tiny per-frame poll from the
// already-resident RuinaNetPanelTick is idempotent and catches every entry
// path into the screen.
//
// Compiles with the in-box C# 5 compiler: no string interpolation, no
// ?. / ?[], no nameof, no expression-bodied members.

using System;
using UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RuinaNet
{
    internal static class CoopEntry
    {
        public const string EntryName = "RuinaNet_CoopEntry";

        private static GameObject _canvasGo;
        private static GameObject _button;
        private static RectTransform _buttonRect;
        private static bool _visible;
        private static UIPhase _lastPhase = UIPhase.DUMMY;

        private static readonly Vector2 ButtonSize = new Vector2(240f, 66f);

        // Measured off the real invitation screen at the 1920x1080 reference
        // resolution: a little to the right of the 邀请函 heading box, and
        // vertically centred on it. Anchor/pivot are top-left for this one.
        private static readonly Vector2 InvitationPos = new Vector2(408f, -34f);

        // Called every frame from RuinaNetPanelTick, which lives on a
        // DontDestroyOnLoad object and therefore outlives every scene.
        public static void Tick()
        {
            // "var" on purpose: the assembly exposes a second UIController type
            // elsewhere, so an explicit name would be ambiguous here.
            var controller = UIPanel.Controller;
            if (controller == null)
            {
                return;
            }

            UIPhase phase = controller.CurrentUIPhase;
            if (phase != _lastPhase)
            {
                // Kept in the shipping build on purpose: this single line is
                // what tells us which screen the player was actually on when
                // the entry button failed to show up.
                Debug.Log("[RuinaNet] ui phase = " + phase);
            }

            bool want = phase == UIPhase.Invitation
                || phase == UIPhase.Invitation_BookList;

            if (!want)
            {
                if (_visible)
                {
                    SetVisible(false);
                }
                _lastPhase = phase;
                return;
            }

            if (_button == null)
            {
                try
                {
                    Build();
                }
                catch (Exception e)
                {
                    Debug.LogError("[RuinaNet] coop entry failed: " + e);
                    return;
                }
            }

            if (phase != _lastPhase)
            {
                Layout();
            }
            if (!_visible)
            {
                SetVisible(true);
            }
            _lastPhase = phase;
        }

        private static void SetVisible(bool on)
        {
            _visible = on;
            if (_canvasGo != null)
            {
                _canvasGo.SetActive(on);
            }
        }

        private static void Layout()
        {
            if (_buttonRect == null)
            {
                return;
            }

            // Invitation screen only: top-left, just right of the "邀请函"
            // heading box, vertically centred on it.
            _buttonRect.anchorMin = new Vector2(0f, 1f);
            _buttonRect.anchorMax = new Vector2(0f, 1f);
            _buttonRect.pivot = new Vector2(0f, 1f);
            _buttonRect.anchoredPosition = InvitationPos;
            _buttonRect.sizeDelta = ButtonSize;
        }

        private static void Build()
        {
            Font font = NetPanel.ResolveFont();

            _canvasGo = new GameObject("RuinaNet_EntryCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);

            Canvas canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32050;

            CanvasScaler scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _canvasGo.AddComponent<GraphicRaycaster>();

            _button = new GameObject(EntryName, typeof(Image));
            _button.transform.SetParent(_canvasGo.transform, false);
            _buttonRect = _button.GetComponent<RectTransform>();

            Image bg = _button.GetComponent<Image>();
            bg.color = new Color(0.10f, 0.10f, 0.13f, 0.95f);
            bg.raycastTarget = true;

            RuinaTheme.Button(_button);

            _button.AddComponent<CoopEntryButton>();

            GameObject labelGo = new GameObject("Label", typeof(Text));
            labelGo.transform.SetParent(_button.transform, false);
            Text label = labelGo.GetComponent<Text>();
            label.font = font;
            label.text = "联 机 接 待";
            label.fontSize = 26;
            label.color = RuinaTheme.Gold;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;

            RectTransform labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            Debug.Log("[RuinaNet] coop entry built (invitation top-left only)");
        }
    }

    // Plain pointer handler: no game component involved, so the creation path
    // cannot inherit a stale UnityEvent from a vanilla button.
    public class CoopEntryButton : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            UISoundManager.instance.PlayEffectSound(UISoundType.Card_Apply);
            NetPanel.Toggle();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            transform.localScale = Vector3.one;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            transform.localScale = new Vector3(1f, 1f, 1f);
        }
    }
}
