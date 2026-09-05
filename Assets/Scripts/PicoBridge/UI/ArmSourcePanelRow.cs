using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built panel row for the arm-source mode mutex (mocap map t07).
    ///
    /// Two pills ("Trackers" / "Body") plus the motion-tracker SN binding
    /// summary. Built entirely from code under the panel's existing control
    /// rows (no prefab edits): if the grandparent stacks rows with a vertical
    /// layout group the row slots in under the server-URL row; otherwise it
    /// clones that row's rect and offsets below it.
    /// </summary>
    public class ArmSourcePanelRow
    {
        private static readonly Color PillSelectedColor = new Color(0.10f, 0.35f, 0.22f, 1f);
        private static readonly Color PillIdleColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);

        private readonly Button _trackersButton;
        private readonly Button _bodyButton;
        private readonly TMP_Text _snText;
        private readonly RectTransform _rowRect;

        private ArmSourcePanelRow(Button trackersButton, Button bodyButton, TMP_Text snText, RectTransform rowRect)
        {
            _trackersButton = trackersButton;
            _bodyButton = bodyButton;
            _snText = snText;
            _rowRect = rowRect;
        }

        /// <summary>The built row's rect — anchor for rows stacked below (t08).</summary>
        public RectTransform RowRect => _rowRect;

        public static ArmSourcePanelRow Build(
            RectTransform templateRow,
            Action onRequestTrackers,
            Action onRequestBody)
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

            var trackersButton = MakePill(rowObject.transform, "Trackers", onRequestTrackers);
            var bodyButton = MakePill(rowObject.transform, "Body", onRequestBody);
            var snText = MakeSnText(rowObject.transform);

            return new ArmSourcePanelRow(trackersButton, bodyButton, snText, rowRect);
        }

        /// <summary>Refresh pill selection (Body active vs Trackers) and the SN summary.</summary>
        public void Refresh(bool bodyActive, string snSummary)
        {
            SetSelected(_trackersButton, !bodyActive);
            SetSelected(_bodyButton, bodyActive);
            if (_snText != null)
            {
                _snText.text = snSummary ?? "--";
                _snText.color = string.IsNullOrEmpty(snSummary) ? MutedTextColor : TextColor;
            }
        }

        private static Button MakePill(Transform parent, string label, Action onClick)
        {
            var pillObject = new GameObject(label + "Button", typeof(RectTransform), typeof(Image), typeof(Button));
            pillObject.transform.SetParent(parent, false);

            var layout = pillObject.AddComponent<LayoutElement>();
            layout.minWidth = 110f;
            layout.minHeight = 44f;
            layout.preferredWidth = 110f;
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
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = TextColor;

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
