using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built panel row for the arm-source mode mutex (mocap t07;
    /// reworked bodytrack-deploy t10, trimmed on the 2026-09-05 in-headset
    /// review): "Gloves" (controllers strapped to the hand backs, mount
    /// correction ON) vs "Held" (normal grip, correction OFF) — both PICO
    /// body mode, the pills are the correction enable switch and double as a
    /// raw-vs-adjusted comparison of the hand cubes. The Trackers pill was
    /// removed per review (tracker fallback stays a receiver-driven ops
    /// action via BridgeControl set_motion). SN binding summary stays. Built
    /// entirely from code under the panel's existing control rows (no prefab
    /// edits).
    /// </summary>
    public class ArmSourcePanelRow
    {
        private static readonly Color PillSelectedColor = new Color(0.10f, 0.35f, 0.22f, 1f);
        private static readonly Color PillIdleColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);

        private readonly Button _glovesButton;
        private readonly Button _heldButton;
        private readonly TMP_Text _snText;
        private readonly RectTransform _rowRect;

        private ArmSourcePanelRow(
            Button glovesButton, Button heldButton,
            TMP_Text snText, RectTransform rowRect)
        {
            _glovesButton = glovesButton;
            _heldButton = heldButton;
            _snText = snText;
            _rowRect = rowRect;
        }

        /// <summary>The built row's rect — anchor for rows stacked below (t08).</summary>
        public RectTransform RowRect => _rowRect;

        public static ArmSourcePanelRow Build(
            RectTransform templateRow,
            Action onRequestGloves,
            Action onRequestHeld)
        {
            if (templateRow == null)
                return null;

            var parent = templateRow.parent as RectTransform;
            var rowObject = new GameObject("ArmSourceControl", typeof(RectTransform));
            var rowRect = (RectTransform)rowObject.transform;
            rowObject.transform.SetParent(parent, false);

            if (parent != null && parent.GetComponent<VerticalLayoutGroup>() != null)
            {
                var rowLayout = rowObject.AddComponent<LayoutElement>();
                rowLayout.minHeight = 44f;
                rowLayout.preferredHeight = 44f;
                rowObject.transform.SetSiblingIndex(templateRow.GetSiblingIndex() + 1);
            }
            else
            {
                rowRect.anchorMin = templateRow.anchorMin;
                rowRect.anchorMax = templateRow.anchorMax;
                rowRect.pivot = templateRow.pivot;
                rowRect.sizeDelta = templateRow.sizeDelta;
                rowRect.anchoredPosition =
                    templateRow.anchoredPosition + Vector2.down * (templateRow.rect.height + 10f);
            }

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var glovesButton = MakePill(rowObject.transform, "Gloves", onRequestGloves);
            var heldButton = MakePill(rowObject.transform, "Held", onRequestHeld);
            var snText = MakeSnText(rowObject.transform);

            return new ArmSourcePanelRow(glovesButton, heldButton, snText, rowRect);
        }

        /// <summary>Refresh pill selection and the SN summary.</summary>
        public void Refresh(bool bodyActive, bool correctionEnabled, string snSummary)
        {
            SetSelected(_glovesButton, bodyActive && correctionEnabled);
            SetSelected(_heldButton, bodyActive && !correctionEnabled);
            if (_snText != null)
            {
                _snText.text = snSummary ?? "--";
                _snText.color = string.IsNullOrEmpty(snSummary) ? MutedTextColor : TextColor;
            }
        }

        private static Button MakePill(Transform parent, string label, Action onClick, bool demoted = false)
        {
            var pillObject = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
            pillObject.transform.SetParent(parent, false);

            float width = demoted ? 82f : 110f;
            var layout = pillObject.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.minHeight = 44f;
            layout.preferredWidth = width;
            layout.preferredHeight = 44f;

            var image = pillObject.GetComponent<Image>();
            image.color = PillIdleColor;

            var button = pillObject.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => onClick?.Invoke());

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(pillObject.transform, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            var text = labelObject.GetComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = demoted ? 21f : 26f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = demoted ? MutedTextColor : TextColor;

            return button;
        }

        private static TMP_Text MakeSnText(Transform parent)
        {
            var textObject = new GameObject("TrackerBinding", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);

            var layout = textObject.AddComponent<LayoutElement>();
            layout.minWidth = 170f;
            layout.minHeight = 44f;
            layout.preferredWidth = 170f;
            layout.preferredHeight = 44f;

            var rect = (RectTransform)textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.Left;
            text.color = MutedTextColor;
            return text;
        }

        private static void SetSelected(Button button, bool selected)
        {
            if (button == null)
                return;
            if (button.targetGraphic is Image image)
                image.color = selected ? PillSelectedColor : PillIdleColor;
        }
    }
}
