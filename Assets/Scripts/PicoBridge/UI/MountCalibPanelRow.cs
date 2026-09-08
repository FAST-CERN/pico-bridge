using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built calibration stepper rows (bodytrack-deploy t08): per side a
    /// yaw stepper (±5°, twist about the hand cube's vertical axis) and a
    /// level stepper (±5 mm slide along that axis — semantics per the
    /// 2026-09-05 in-headset review), stacked under the arm-source row.
    ///
    /// Clicks go straight through the owner's callback into
    /// Tracking.BodyMountCorrection.SetSide, so an adjustment applies to the
    /// body output on the next collected frame and persists as the boot
    /// default (t07 store) — the hand cubes move immediately and the
    /// receiver sees the joints move.
    /// </summary>
    public class MountCalibPanelRow
    {
        public const float YawStepDegrees = 5f;
        public const float LevelStepMillimetres = 5f;
        public const float MaxYawDegrees = 90f;
        public const float MaxLevelMillimetres = 150f;

        private static readonly Color StepperColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);

        private readonly TMP_Text _leftYaw;
        private readonly TMP_Text _leftLevel;
        private readonly TMP_Text _rightYaw;
        private readonly TMP_Text _rightLevel;
        private readonly GameObject _rowLeft;
        private readonly GameObject _rowRight;

        private MountCalibPanelRow(
            GameObject rowLeft, GameObject rowRight,
            TMP_Text leftYaw, TMP_Text leftLevel, TMP_Text rightYaw, TMP_Text rightLevel)
        {
            _rowLeft = rowLeft;
            _rowRight = rowRight;
            _leftYaw = leftYaw;
            _leftLevel = leftLevel;
            _rightYaw = rightYaw;
            _rightLevel = rightLevel;
        }

        /// <summary>Build the L and R stepper rows under the anchor row.
        /// ``onAdjust(side, isYaw, delta)`` fires per click.</summary>
        public static MountCalibPanelRow Build(RectTransform anchorRow, Action<string, bool, float> onAdjust)
        {
            if (anchorRow == null)
                return null;

            var leftYaw = (TMP_Text)null;
            var leftLevel = (TMP_Text)null;
            var rightYaw = (TMP_Text)null;
            var rightLevel = (TMP_Text)null;
            GameObject rowLeft = null;
            GameObject rowRight = null;

            var anchor = anchorRow;
            foreach (var side in new[] { "L", "R" })
            {
                var row = BuildRow(anchor, side,
                    onYaw: d => onAdjust?.Invoke(side == "L" ? "left" : "right", true, d),
                    onLevel: d => onAdjust?.Invoke(side == "L" ? "left" : "right", false, d));
                anchor = row;
                if (side == "L")
                    rowLeft = row.gameObject;
                else
                    rowRight = row.gameObject;
            }

            var rowObjectL = anchorRow.parent.Find("MountCalibL");
            var rowObjectR = anchorRow.parent.Find("MountCalibR");
            if (rowObjectL != null)
            {
                leftYaw = FindText(rowObjectL, "YawValue");
                leftLevel = FindText(rowObjectL, "LevelValue");
            }
            if (rowObjectR != null)
            {
                rightYaw = FindText(rowObjectR, "YawValue");
                rightLevel = FindText(rowObjectR, "LevelValue");
            }

            return new MountCalibPanelRow(
                rowLeft, rowRight, leftYaw, leftLevel, rightYaw, rightLevel);
        }

        private static RectTransform BuildRow(
            RectTransform anchorRow,
            string side,
            Action<float> onYaw,
            Action<float> onLevel)
        {
            var parent = anchorRow.parent as RectTransform;
            var rowObject = new GameObject("MountCalib" + side, typeof(RectTransform));
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
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            MakeLabel(rowObject.transform, side, width: 30f, color: MutedTextColor);
            MakeStepper(rowObject.transform, "yaw", onYaw);
            MakeStepper(rowObject.transform, "lev", onLevel);

            return rowRect;
        }

        private static void MakeStepper(Transform parent, string param, Action<float> onDelta)
        {
            float step = param == "yaw" ? YawStepDegrees : LevelStepMillimetres;
            MakeLabel(parent, param, width: 52f, color: MutedTextColor, fontSize: 22f);
            MakeButton(parent, "-", () => onDelta(-step));
            MakeValueText(parent, param == "yaw" ? "YawValue" : "LevelValue");
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
            layout.minWidth = 48f;
            layout.preferredWidth = 48f;

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

        private static TMP_Text MakeValueText(Transform parent, string name)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);
            var layout = textObject.AddComponent<LayoutElement>();
            layout.minWidth = 84f;
            layout.preferredWidth = 84f;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = TextColor;
            return text;
        }

        private static TMP_Text FindText(Transform row, string name)
        {
            var found = row.Find(name);
            return found != null ? found.GetComponent<TMP_Text>() : null;
        }

        /// <summary>Body-mode tuning only (t04 ②): show/hide both stepper
        /// rows — the knobs are strapped-controller correction params and are
        /// irrelevant (and visually noisy) in tracker mode.</summary>
        public void SetVisible(bool visible)
        {
            if (_rowLeft != null)
                _rowLeft.SetActive(visible);
            if (_rowRight != null)
                _rowRight.SetActive(visible);
        }

        /// <summary>Update the four value texts from the correction store.</summary>
        public void Refresh()
        {
            RefreshSide("left", _leftYaw, _leftLevel);
            RefreshSide("right", _rightYaw, _rightLevel);
        }

        private static void RefreshSide(string side, TMP_Text yawText, TMP_Text levelText)
        {
            var entry = Tracking.BodyMountCorrection.GetSide(side);
            if (entry == null)
                return;
            bool active = Tracking.BodyMountCorrection.Enabled;
            if (yawText != null)
            {
                yawText.text = $"{entry.yaw:0.#}°";
                yawText.color = active ? TextColor : MutedTextColor;
            }
            if (levelText != null)
            {
                levelText.text = $"{entry.level:0.#}mm";
                levelText.color = active ? TextColor : MutedTextColor;
            }
        }
    }
}
