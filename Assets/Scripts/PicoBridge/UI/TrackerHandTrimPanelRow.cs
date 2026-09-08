using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built trim stepper rows (tracker-ik map t17, human-trim
    /// paradigm): per side yaw/pitch/roll steppers (±5°, the uncontrolled
    /// strap rotation) and a level stepper (±5 mm slide along the trimmed
    /// hand's up axis) — the MountCalibPanelRow pattern with two more
    /// rotation axes. Clicks go through the owner's callback into
    /// Tracking.TrackerHandCalibration.SetSide, so an adjustment applies
    /// on the next viz frame and persists as the boot default; the
    /// operator aligns the bright mapped-hand gizmo onto their passthrough
    /// view of the real hand (~30 s once per strapping).
    /// </summary>
    public class TrackerHandTrimPanelRow
    {
        public const float RotStepDegrees = 5f;
        public const float LevelStepMillimetres = 5f;
        public const float MaxRotDegrees = 90f;
        public const float MaxLevelMillimetres = 150f;

        private static readonly Color StepperColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);

        private readonly GameObject _rowLeft;
        private readonly GameObject _rowRight;
        private readonly TMP_Text[] _leftValues = new TMP_Text[4];
        private readonly TMP_Text[] _rightValues = new TMP_Text[4];

        private TrackerHandTrimPanelRow(GameObject rowLeft, GameObject rowRight)
        {
            _rowLeft = rowLeft;
            _rowRight = rowRight;
        }

        /// <summary>Build the L and R trim rows under the anchor row.
        /// ``onAdjust(side, axis, delta)`` fires per click; axis is
        /// "yaw" | "pitch" | "roll" | "level".</summary>
        public static TrackerHandTrimPanelRow Build(
            RectTransform anchorRow, Action<string, string, float> onAdjust)
        {
            if (anchorRow == null)
                return null;

            GameObject rowLeft = null;
            GameObject rowRight = null;
            var anchor = anchorRow;
            foreach (var side in new[] { "L", "R" })
            {
                var row = BuildRow(anchor, side,
                    (axis, d) => onAdjust?.Invoke(side == "L" ? "left" : "right", axis, d));
                anchor = row;
                if (side == "L")
                    rowLeft = row.gameObject;
                else
                    rowRight = row.gameObject;
            }

            var result = new TrackerHandTrimPanelRow(rowLeft, rowRight);
            var rowObjectL = anchorRow.parent.Find("HandTrimL");
            var rowObjectR = anchorRow.parent.Find("HandTrimR");
            result.CollectValues(rowObjectL, result._leftValues);
            result.CollectValues(rowObjectR, result._rightValues);
            return result;
        }

        private static RectTransform BuildRow(
            RectTransform anchorRow,
            string side,
            Action<string, float> onAxis)
        {
            var parent = anchorRow.parent as RectTransform;
            var rowObject = new GameObject("HandTrim" + side, typeof(RectTransform));
            var rowRect = (RectTransform)rowObject.transform;
            rowObject.transform.SetParent(parent, false);

            if (parent != null && parent.GetComponent<VerticalLayoutGroup>() != null)
            {
                var rowLayout = rowObject.AddComponent<LayoutElement>();
                rowLayout.minHeight = 40f;
                rowLayout.preferredHeight = 40f;
                rowObject.transform.SetSiblingIndex(anchorRow.GetSiblingIndex() + 1);
            }
            else
            {
                rowRect.anchorMin = anchorRow.anchorMin;
                rowRect.anchorMax = anchorRow.anchorMax;
                rowRect.pivot = anchorRow.pivot;
                rowRect.sizeDelta = anchorRow.sizeDelta;
                rowRect.anchoredPosition =
                    anchorRow.anchoredPosition + Vector2.down * (anchorRow.rect.height + 8f);
            }

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            MakeLabel(rowObject.transform, side, width: 26f, color: MutedTextColor);
            MakeStepper(rowObject.transform, "yaw", "YawValue", 40f, d => onAxis("yaw", d), RotStepDegrees);
            MakeStepper(rowObject.transform, "pit", "PitchValue", 40f, d => onAxis("pitch", d), RotStepDegrees);
            MakeStepper(rowObject.transform, "rol", "RollValue", 40f, d => onAxis("roll", d), RotStepDegrees);
            MakeStepper(rowObject.transform, "lev", "LevelValue", 44f, d => onAxis("level", d), LevelStepMillimetres);

            return rowRect;
        }

        private static void MakeStepper(
            Transform parent, string param, string valueName, float valueWidth,
            Action<float> onDelta, float step)
        {
            MakeLabel(parent, param, width: 40f, color: MutedTextColor, fontSize: 22f);
            MakeButton(parent, "-", () => onDelta(-step));
            MakeValueText(parent, valueName, valueWidth);
            MakeButton(parent, "+", () => onDelta(step));
        }

        private static TMP_Text MakeLabel(Transform parent, string label, float width, Color color, float fontSize = 26f)
        {
            var textObject = new GameObject("Label_" + label, typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);
            var layout = textObject.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Left;
            text.color = color;
            return text;
        }

        private static Button MakeButton(Transform parent, string label, Action onClick)
        {
            var buttonObject = new GameObject("Stepper" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            var layout = buttonObject.AddComponent<LayoutElement>();
            layout.minWidth = 42f;
            layout.preferredWidth = 42f;

            var image = buttonObject.GetComponent<Image>();
            image.color = StepperColor;

            var button = buttonObject.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => onClick?.Invoke());

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(buttonObject.transform, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            var text = labelObject.GetComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 30f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = TextColor;

            return button;
        }

        private static TMP_Text MakeValueText(Transform parent, string name, float width)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);
            var layout = textObject.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = 24f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = TextColor;
            return text;
        }

        private void CollectValues(Transform row, TMP_Text[] values)
        {
            if (row == null)
                return;
            var names = new[] { "YawValue", "PitchValue", "RollValue", "LevelValue" };
            for (int i = 0; i < names.Length; i++)
            {
                var found = row.Find(names[i]);
                if (found != null)
                    values[i] = found.GetComponent<TMP_Text>();
            }
        }

        /// <summary>Tracker-mode tuning only (mirror of the body-mode
        /// mount-calib rows): show/hide both stepper rows.</summary>
        public void SetVisible(bool visible)
        {
            if (_rowLeft != null)
                _rowLeft.SetActive(visible);
            if (_rowRight != null)
                _rowRight.SetActive(visible);
        }

        /// <summary>Update the eight value texts from the trim store.</summary>
        public void Refresh()
        {
            RefreshSide("left", _leftValues);
            RefreshSide("right", _rightValues);
        }

        private static void RefreshSide(string side, TMP_Text[] values)
        {
            var entry = Tracking.TrackerHandCalibration.GetSide(side);
            if (entry == null)
                return;
            var texts = new[]
            {
                $"{entry.yaw:0.#}°",
                $"{entry.pitch:0.#}°",
                $"{entry.roll:0.#}°",
                $"{entry.level:0.#}mm",
            };
            for (int i = 0; i < 4; i++)
                if (values[i] != null)
                    values[i].text = texts[i];
        }
    }
}
