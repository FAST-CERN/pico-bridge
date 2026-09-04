using TMPro;
using UnityEngine;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Head-locked "how to exit" hint shown for a few seconds when immersive
    /// mode starts (UI grill 2026-09-02: demo console — a visitor needs the
    /// grip exit spelled out once). English only: the bundled TMP font has no
    /// CJK glyphs. Runtime-assembled by StereoImmersiveBootstrap.
    /// </summary>
    [DisallowMultipleComponent]
    public class StereoImmersiveExitHint : MonoBehaviour
    {
        [Tooltip("Seconds at full opacity before fading out.")]
        [SerializeField] private float visibleSeconds = 3f;
        [Tooltip("Fade-out duration in seconds.")]
        [SerializeField] private float fadeSeconds = 0.8f;

        private TextMeshPro _text;
        private float _shownAt = -1f;
        private bool _active;

        public void Build(Transform rigRoot, float distance)
        {
            var font = TMP_Settings.defaultFontAsset;
            if (font == null)
            {
                Debug.LogWarning("[StereoImmersive] no default TMP font asset; exit hint disabled");
                return;
            }

            var go = new GameObject("ExitHint");
            go.transform.SetParent(rigRoot, false);
            go.transform.localPosition = new Vector3(0f, -0.55f, distance - 0.05f);
            go.transform.localRotation = Quaternion.identity;
            // World-space TMP is ~0.1m per font-size unit: scale 0.045 with
            // fontSize 14 renders ~6cm glyphs (0.35 rendered half-metre text).
            go.transform.localScale = new Vector3(0.045f, 0.045f, 1f);

            _text = go.AddComponent<TextMeshPro>();
            _text.font = font;
            _text.fontSize = 14;
            _text.alignment = TextAlignmentOptions.Center;
            _text.text = "Grip (right controller) to exit";
            _text.color = new Color(0.92f, 0.96f, 0.98f, 0.85f);
            _text.raycastTarget = false;
            _text.rectTransform.sizeDelta = new Vector2(60f, 6f);
            _text.gameObject.SetActive(false);
        }

        /// <summary>Show now at full opacity; fades itself out.</summary>
        public void Show()
        {
            if (_text == null) return;
            _active = true;
            _shownAt = Time.realtimeSinceStartup;
            var c = _text.color;
            c.a = 1f;
            _text.color = c;
            _text.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!_active || _text == null) return;
            float elapsed = Time.realtimeSinceStartup - _shownAt;
            if (elapsed < visibleSeconds) return;
            float fade = 1f - Mathf.Clamp01((elapsed - visibleSeconds) / fadeSeconds);
            if (fade <= 0f)
            {
                _active = false;
                _text.gameObject.SetActive(false);
                return;
            }
            var c = _text.color;
            c.a = fade;
            _text.color = c;
        }
    }
}
