using System;
using UnityEngine;
using UnityEngine.UI;

namespace ClosingShift.Localization
{
    internal static class RussianAttribution
    {
        internal const string ChildName = "ClosingShift.RussianAttribution";
        internal const string Credit = "Перевод на русский:\nChatGPT (OpenAI)";
        private static WeakReference current;

        internal static void Hide()
        {
            Text text = current == null ? null : current.Target as Text;
            if (text != null) text.gameObject.SetActive(false);
        }

        internal static void Apply(Dropdown owner)
        {
            if (owner == null || owner.captionText == null) return;
            Text text = current == null ? null : current.Target as Text;
            if (text != null && text.transform.parent != owner.transform)
            {
                text.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(text.gameObject);
                text = null;
            }
            if (text == null)
            {
                // The reserved child belongs to this helper; reuse it across repeated localization.
                for (int i = owner.transform.childCount - 1; i >= 0; --i)
                {
                    Transform child = owner.transform.GetChild(i);
                    if (child.name != ChildName) continue;
                    Text existing = child.GetComponent<Text>();
                    if (text == null && existing != null) text = existing;
                    else
                    {
                        child.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(child.gameObject);
                    }
                }
                if (text == null)
                {
                    var child = new GameObject(ChildName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                    child.layer = owner.gameObject.layer;
                    child.transform.SetParent(owner.transform, false);
                    text = child.GetComponent<Text>();
                    Debug.Log("PORT_RU_ATTRIBUTION owner=Canvas/Menu/Dropdown credit=ChatGPT(OpenAI)");
                }
                current = new WeakReference(text);
            }
            Text caption = owner.captionText;
            var rect = (RectTransform)text.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(1, 1);
            // Five authored options occupy 108 units below the template top; keep the credit below them.
            rect.anchoredPosition = new Vector2(0, -110);
            rect.sizeDelta = new Vector2(260, 56);
            rect.localScale = Vector3.one;
            text.font = caption.font;
            text.fontStyle = caption.fontStyle;
            text.material = caption.material;
            text.color = Color.white;
            text.lineSpacing = caption.lineSpacing;
            text.maskable = caption.maskable;
            text.fontSize = 20;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 18;
            text.resizeTextMaxSize = 20;
            text.alignment = TextAnchor.UpperCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.text = Credit;
            text.gameObject.SetActive(true);
            RussianFontCompat.ApplyTo(text);
        }
    }
}
