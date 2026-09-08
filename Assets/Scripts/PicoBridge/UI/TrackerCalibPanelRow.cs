using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built calibration entry row (tracker-ik map t06): a CALIB/ABORT
    /// button plus a status label that carries the in-app pose guidance and
    /// countdowns from Tracking.TrackerCalibrationGuide. The 2026-09-08
    /// device round moved guidance in-headset: PC audio beats mistimed the
    /// operator (0.325 m rejection), so the panel IS the metronome now.
    ///
    /// Built under the arm-source row; visible only in tracker mode
    /// (TrackerBody) — the mirror of the mount-calib rows' body-mode gating
    /// (t04). No prefab edits.
    /// </summary>
    public class TrackerCalibPanelRow
    {
        private static readonly Color ButtonColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color ButtonAbortColor = new Color(0.62f, 0.20f, 0.16f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);
        private static readonly Color UrgentTextColor = new Color(0.96f, 0.40f, 0.30f, 1f);

        private readonly Button _button;
        private readonly TMP_Text _buttonLabel;
        private readonly TMP_Text _statusText;
        private readonly GameObject _rowObject;

        private TrackerCalibPanelRow(GameObject rowObject, Button button, TMP_Text buttonLabel, TMP_Text statusText)
        {
            _rowObject = rowObject;
            _button = button;
            _buttonLabel = buttonLabel;
            _statusText = statusText;
        }

        /// <summary>Build the row directly under the anchor row.
        /// ``onToggle`` fires on button click (start when idle, abort when
        /// the guide is running — the controller routes it).</summary>
        public static TrackerCalibPanelRow Build(RectTransform anchorRow, Action onToggle)
        {
            if (anchorRow == null)
                return null;

            var parent = anchorRow.parent as RectTransform;
            var rowObject = new GameObject("TrackerCalib", typeof(RectTransform));
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

            var button = MakeButton(rowObject.transform, onToggle);
            var buttonLabel = button.GetComponentInChildren<TMP_Text>();

            var textObject = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(rowObject.transform, false);
            var textLayout = textObject.AddComponent<LayoutElement>();
            textLayout.minWidth = 560f;
            textLayout.preferredWidth = 560f;
            var statusText = textObject.GetComponent<TextMeshProUGUI>();
            statusText.fontSize = 26f;
            statusText.alignment = TextAlignmentOptions.Left;
            statusText.color = MutedTextColor;

            return new TrackerCalibPanelRow(rowObject, button, buttonLabel, statusText);
        }

        private static Button MakeButton(Transform parent, Action onClick)
        {
            var buttonObject = new GameObject("CalibButton", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            var layout = buttonObject.AddComponent<LayoutElement>();
            layout.minWidth = 110f;
            layout.preferredWidth = 110f;

            var image = buttonObject.GetComponent<Image>();
            image.color = ButtonColor;

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
            text.text = "CALIB";
            text.fontSize = 28f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = TextColor;

            return button;
        }

        /// <summary>Tracker-mode only (t06): shown when the arm stream is
        /// the device tracker chain, hidden otherwise — mirror of the
        /// mount-calib rows' body-mode gating.</summary>
        public void SetVisible(bool visible)
        {
            if (_rowObject != null)
                _rowObject.SetActive(visible);
        }

        /// <summary>Per-refresh-chain update: button label/colour by run
        /// state, status copy from the guide (red on the urgent beat).</summary>
        public void Refresh(bool guideRunning, string status, bool urgent)
        {
            if (_buttonLabel != null)
                _buttonLabel.text = guideRunning ? "ABORT" : "CALIB";
            if (_button != null && _button.image != null)
                _button.image.color = guideRunning ? ButtonAbortColor : ButtonColor;
            if (_statusText != null)
            {
                _statusText.text = status;
                _statusText.color = urgent ? UrgentTextColor : guideRunning ? TextColor : MutedTextColor;
            }
        }
    }
}
