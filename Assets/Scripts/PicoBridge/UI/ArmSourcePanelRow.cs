using System;
using PicoBridge.Tracking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.UI
{
    /// <summary>
    /// Code-built panel row for the arm-source mode mutex (mocap t07;
    /// reworked bodytrack-deploy t10; third state restored tracker-ik t04):
    /// "Trackers" (the device-side tracker chain emitting 24-joint body
    /// frames — ArmStreamMode.TrackerBody) vs "Gloves" (controllers strapped
    /// to the hand backs, mount correction ON) vs "Held" (normal grip,
    /// correction OFF) — Gloves/Held are both PICO body mode, the pills are
    /// the correction enable switch. The legacy Motion-payload Trackers
    /// (receiver synth) stays receiver-gated via BridgeControl set_motion
    /// and lights no pill. Built entirely from code under the panel's
    /// existing control rows (no prefab edits).
    ///
    /// t04 UX: two LED status dots (round, runtime-generated sprite) sit
    /// between the pills and the SN text — green=valid, yellow=optical
    /// ghost, red=disconnected, gray=unbound (TrackerSessionStatus per
    /// side); the SN summary text stays as secondary detail.
    /// </summary>
    public class ArmSourcePanelRow
    {
        private static readonly Color PillSelectedColor = new Color(0.10f, 0.35f, 0.22f, 1f);
        private static readonly Color PillIdleColor = new Color(0.16f, 0.19f, 0.205f, 1f);
        private static readonly Color TextColor = new Color(0.94f, 0.975f, 0.985f, 1f);
        private static readonly Color MutedTextColor = new Color(0.66f, 0.72f, 0.75f, 1f);

        // LED four-color semantics (2026-09-08 in-headset review, t04 ③).
        private static readonly Color LedValidColor = new Color(0.16f, 0.74f, 0.43f, 1f);
        private static readonly Color LedLostColor = new Color(0.96f, 0.63f, 0.16f, 1f);
        private static readonly Color LedDisconnectedColor = new Color(0.88f, 0.22f, 0.29f, 1f);
        private static readonly Color LedUnboundColor = new Color(0.45f, 0.49f, 0.52f, 1f);

        private static Sprite _dotSprite;

        private readonly Button _trackersButton;
        private readonly Button _glovesButton;
        private readonly Button _heldButton;
        private readonly Image _ledLeft;
        private readonly Image _ledRight;
        private readonly TMP_Text _snText;
        private readonly RectTransform _rowRect;

        private ArmSourcePanelRow(
            Button trackersButton, Button glovesButton, Button heldButton,
            Image ledLeft, Image ledRight,
            TMP_Text snText, RectTransform rowRect)
        {
            _trackersButton = trackersButton;
            _glovesButton = glovesButton;
            _heldButton = heldButton;
            _ledLeft = ledLeft;
            _ledRight = ledRight;
            _snText = snText;
            _rowRect = rowRect;
        }

        /// <summary>The built row's rect — anchor for rows stacked below (t08).</summary>
        public RectTransform RowRect => _rowRect;

        public static ArmSourcePanelRow Build(
            RectTransform templateRow,
            Action onRequestGloves,
            Action onRequestHeld,
            Action onRequestTrackers)
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
            var glovesButton = MakePill(rowObject.transform, "Gloves", onRequestGloves);
            var heldButton = MakePill(rowObject.transform, "Held", onRequestHeld);
            var ledLeft = MakeLedDot(rowObject.transform, "LedLeft");
            var ledRight = MakeLedDot(rowObject.transform, "LedRight");
            var snText = MakeSnText(rowObject.transform);

            return new ArmSourcePanelRow(
                trackersButton, glovesButton, heldButton, ledLeft, ledRight, snText, rowRect);
        }

        /// <summary>Refresh pill selection, LED colors, and the SN summary.</summary>
        public void Refresh(
            PicoBridgeManager.ArmStreamMode mode,
            bool correctionEnabled,
            string snSummary,
            TrackerSideState leftState,
            TrackerSideState rightState)
        {
            // Trackers idle (legacy Motion-payload default) lights nothing;
            // TrackerBody is the device-side tracker chain (t04).
            SetSelected(_trackersButton, mode == PicoBridgeManager.ArmStreamMode.TrackerBody);
            SetSelected(_glovesButton, mode == PicoBridgeManager.ArmStreamMode.Body && correctionEnabled);
            SetSelected(_heldButton, mode == PicoBridgeManager.ArmStreamMode.Body && !correctionEnabled);
            SetLed(_ledLeft, leftState);
            SetLed(_ledRight, rightState);
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

        // Round status dot: 18x18 Image tinted by Refresh. The disc sprite is
        // generated once at runtime (anti-aliased alpha edge) so the row
        // stays prefab-free.
        private static Image MakeLedDot(Transform parent, string name)
        {
            var dotObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            dotObject.transform.SetParent(parent, false);

            var layout = dotObject.AddComponent<LayoutElement>();
            layout.minWidth = 18f;
            layout.minHeight = 18f;
            layout.preferredWidth = 18f;
            layout.preferredHeight = 18f;

            var image = dotObject.GetComponent<Image>();
            image.sprite = DotSprite;
            image.color = LedUnboundColor;
            image.raycastTarget = false;
            return image;
        }

        private static Sprite DotSprite
        {
            get
            {
                if (_dotSprite != null)
                    return _dotSprite;

                const int size = 32;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.hideFlags = HideFlags.DontSave;
                texture.wrapMode = TextureWrapMode.Clamp;
                var pixels = new Color32[size * size];
                float center = (size - 1) * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - center;
                        float dy = y - center;
                        float distance = Mathf.Sqrt(dx * dx + dy * dy);
                        // One-pixel anti-aliased falloff at the disc edge.
                        float alpha = Mathf.Clamp01(center + 0.5f - distance);
                        byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                        pixels[y * size + x] = new Color32(255, 255, 255, a);
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                _dotSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
                return _dotSprite;
            }
        }

        private static void SetLed(Image dot, TrackerSideState state)
        {
            if (dot == null)
                return;
            switch (state)
            {
                case TrackerSideState.Valid:
                    dot.color = LedValidColor;
                    break;
                case TrackerSideState.OpticalLost:
                    dot.color = LedLostColor;
                    break;
                case TrackerSideState.Disconnected:
                    dot.color = LedDisconnectedColor;
                    break;
                default:
                    dot.color = LedUnboundColor;
                    break;
            }
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
