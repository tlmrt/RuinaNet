using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UI;

namespace RuinaNet
{
    internal static class RuinaTheme
    {
        private static Image _panelTemplate;
        private static Button _buttonTemplate;
        private static InputField _inputTemplate;
        private static bool _templatesSearched;
        public static readonly Color Ink = new Color(0.035f, 0.031f, 0.025f, 0.98f);
        public static readonly Color Field = new Color(0.065f, 0.054f, 0.039f, 1f);
        public static readonly Color Paper = new Color(0.88f, 0.83f, 0.70f, 1f);
        public static readonly Color Muted = new Color(0.60f, 0.55f, 0.43f, 1f);
        public static Color Gold
        {
            get { try { return UI.UIColorManager.Manager.GetUIColor(UI.UIColor.Default); }
                  catch (Exception) { return new Color(0.72f, 0.57f, 0.30f, 1f); } }
        }
        public static Color Highlight
        {
            get { try { return UI.UIColorManager.Manager.GetUIColor(UI.UIColor.Highlighted); }
                  catch (Exception) { return new Color(1f, 0.82f, 0.40f, 1f); } }
        }

        public static void Frame(GameObject go, bool ornate)
        {
            Image bg = go.GetComponent<Image>();
            EnsureTemplates();
            if (bg != null)
            {
                if (_panelTemplate != null) CopyImage(_panelTemplate, bg);
                else bg.color = Ink;
            }
            // Native sliced popup art already contains its own border.  The small
            // generated edge is retained only as a safe fallback on unusual scenes.
            if (_panelTemplate != null) return;
            Edge(go.transform, "FrameTop", new Vector2(0,1), new Vector2(1,1), new Vector2(0,-1), new Vector2(0,1));
            Edge(go.transform, "FrameBottom", Vector2.zero, new Vector2(1,0), new Vector2(0,-1), new Vector2(0,1));
            Edge(go.transform, "FrameLeft", Vector2.zero, new Vector2(0,1), new Vector2(-1,0), new Vector2(1,0));
            Edge(go.transform, "FrameRight", new Vector2(1,0), Vector2.one, new Vector2(-1,0), new Vector2(1,0));
            if (ornate)
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector2 anchor = new Vector2(i % 2, i / 2);
                    Image diamond = Edge(go.transform, "Seal", anchor, anchor, new Vector2(-4,-4), new Vector2(4,4));
                    diamond.transform.localRotation = Quaternion.Euler(0,0,45);
                }
            }
        }

        private static Image Edge(Transform parent, string name, Vector2 min, Vector2 max, Vector2 lo, Vector2 hi)
        {
            GameObject go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = Gold;
            image.raycastTarget = false;
            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = min; r.anchorMax = max;
            r.offsetMin = lo; r.offsetMax = hi;
            return image;
        }

        public static void Button(GameObject go)
        {
            EnsureTemplates();
            Button b = go.GetComponent<Button>();
            Image image = go.GetComponent<Image>();
            if (_buttonTemplate != null)
            {
                Image source = _buttonTemplate.targetGraphic as Image;
                if (source == null) source = _buttonTemplate.GetComponent<Image>();
                if (source != null && image != null) CopyImage(source, image);
                if (b != null) b.colors = _buttonTemplate.colors;
            }
            else
            {
                Frame(go, false);
            }
            if (b != null && _buttonTemplate == null)
            {
                ColorBlock colors = b.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(2f,1.8f,1.4f,1f);
                colors.pressedColor = new Color(3f,2.5f,1.5f,1f);
                colors.disabledColor = new Color(0.45f,0.45f,0.45f,0.65f);
                colors.fadeDuration = 0.12f;
                b.colors = colors;
            }
            go.AddComponent<RuinaThemeHover>();
        }

        public static void Input(GameObject go)
        {
            EnsureTemplates();
            Image image = go.GetComponent<Image>();
            if (_inputTemplate != null)
            {
                Image source = _inputTemplate.targetGraphic as Image;
                if (source == null) source = _inputTemplate.GetComponent<Image>();
                if (source != null && image != null) CopyImage(source, image);
            }
            else
            {
                Frame(go, false);
                if (image != null) image.color = Field;
            }
            InputField field = go.GetComponent<InputField>();
            if (field != null) { field.customCaretColor = true; field.caretColor = Gold; field.selectionColor = new Color(0.7f,0.5f,0.2f,0.35f); }
        }

        private static bool IsOurs(Component component)
        {
            if (component == null) return true;
            Transform t = component.transform;
            while (t != null)
            {
                if (t.name != null && t.name.StartsWith("RuinaNet", StringComparison.Ordinal)) return true;
                t = t.parent;
            }
            return false;
        }

        private static void EnsureTemplates()
        {
            if (_templatesSearched && (_panelTemplate != null || _buttonTemplate != null)) return;
            _templatesSearched = true;
            try
            {
                Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button candidate = buttons[i];
                    Image image = candidate == null ? null : candidate.targetGraphic as Image;
                    if (!IsOurs(candidate) && image != null && image.sprite != null)
                    {
                        _buttonTemplate = candidate;
                        break;
                    }
                }

                InputField[] inputs = Resources.FindObjectsOfTypeAll<InputField>();
                for (int i = 0; i < inputs.Length; i++)
                {
                    InputField candidate = inputs[i];
                    Image image = candidate == null ? null : candidate.targetGraphic as Image;
                    if (!IsOurs(candidate) && image != null && image.sprite != null)
                    {
                        _inputTemplate = candidate;
                        break;
                    }
                }

                UIAlarmPopup[] popups = Resources.FindObjectsOfTypeAll<UIAlarmPopup>();
                float bestArea = 0f;
                for (int p = 0; p < popups.Length; p++)
                {
                    Image[] images = popups[p].GetComponentsInChildren<Image>(true);
                    for (int i = 0; i < images.Length; i++)
                    {
                        Image candidate = images[i];
                        if (candidate.sprite == null || candidate.type != Image.Type.Sliced) continue;
                        RectTransform rect = candidate.rectTransform;
                        float area = Mathf.Abs(rect.rect.width * rect.rect.height);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            _panelTemplate = candidate;
                        }
                    }
                }
                Debug.Log("[RuinaNet][ui] native templates panel=" + (_panelTemplate != null) +
                          " button=" + (_buttonTemplate != null) + " input=" + (_inputTemplate != null));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ui] native template lookup failed: " + e.Message);
            }
        }

        private static void CopyImage(Image source, Image target)
        {
            target.sprite = source.sprite;
            target.overrideSprite = source.overrideSprite;
            target.type = source.type;
            target.preserveAspect = source.preserveAspect;
            target.fillCenter = source.fillCenter;
            target.fillMethod = source.fillMethod;
            target.fillOrigin = source.fillOrigin;
            target.fillClockwise = source.fillClockwise;
            target.fillAmount = source.fillAmount;
            target.material = source.material;
            target.color = source.color.a < 0.05f ? Color.white : source.color;
        }
    }

    internal sealed class RuinaThemeHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private void Paint(bool hot)
        {
            Button button = GetComponent<Button>();
            if (button != null && !button.interactable) hot = false;
            foreach (Text text in GetComponentsInChildren<Text>()) text.color = hot ? RuinaTheme.Highlight : RuinaTheme.Paper;
            foreach (Image image in GetComponentsInChildren<Image>())
                if (image.gameObject != gameObject) image.color = hot ? RuinaTheme.Highlight : RuinaTheme.Gold;
        }
        public void OnPointerEnter(PointerEventData data) { Paint(true); }
        public void OnPointerExit(PointerEventData data) { Paint(false); }
        private void OnDisable() { Paint(false); }
        public void OnPointerClick(PointerEventData data)
        {
            Button b = GetComponent<Button>();
            if (b != null && b.interactable)
                try { UISoundManager.instance.PlayEffectSound(UISoundType.Card_Apply); } catch (Exception) { }
        }
    }
}
