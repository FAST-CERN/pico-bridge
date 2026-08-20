using UnityEngine;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Head-locked stereoscopic FPV screen: a single quad in front of the
    /// headset that renders a side-by-side frame through the
    /// PicoBridge/StereoSbsQuad stereo shader (per-eye half sampling).
    /// Geometry follows the televuer immersive-mode recipe: rigid head lock,
    /// explicit size independent of camera FOV, unlit, dark surround.
    /// </summary>
    [DisallowMultipleComponent]
    public class StereoImmersiveRig : MonoBehaviour
    {
        [Header("Video")]
        [Tooltip("SBS texture source. Wired to WebRtcCameraReceiver at runtime; any Texture works.")]
        [SerializeField] private Texture videoTexture;

        [Header("Placement (head-locked)")]
        [Tooltip("Distance from the eyes to the screen, in metres.")]
        [SerializeField] private float distance = 2.0f;
        [Tooltip("Screen height in metres. Width follows the per-eye aspect ratio.")]
        [SerializeField] private float height = 1.66f;
        [Tooltip("Per-eye image aspect (ZED 720p per eye = 640x480 -> 4:3 = 1.3333).")]
        [SerializeField] private float eyeAspect = 4f / 3f;
        [Tooltip("Vertical offset from eye level, in metres (0 = centred, televuer default).")]
        [SerializeField] private float verticalOffset;

        [Header("Behaviour")]
        [Tooltip("Follow head pose every LateUpdate (rigid head lock).")]
        [SerializeField] private bool headLocked = true;

        private Transform _head;
        private Renderer _screenRenderer;
        private MaterialPropertyBlock _props;

        public bool HasVideoTexture => videoTexture != null;

        private void Awake()
        {
            _screenRenderer = GetComponentInChildren<Renderer>();
            _props = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            ResolveHead();
            ApplyGeometry();
        }

        private void LateUpdate()
        {
            if (!headLocked) return;
            if (_head == null && !ResolveHead()) return;
            ApplyGeometry();
        }

        /// <summary>Feed a new SBS texture (WebRTC video track, test source, ...).</summary>
        public void SetVideoTexture(Texture texture)
        {
            videoTexture = texture;
            PushTexture();
        }

        /// <summary>Override placement values before first use (bootstrap wiring).</summary>
        public void Configure(float newDistance, float newHeight, float newEyeAspect, float newVerticalOffset = 0f)
        {
            distance = newDistance;
            height = newHeight;
            eyeAspect = newEyeAspect;
            verticalOffset = newVerticalOffset;
            ApplyGeometry();
        }

        private bool ResolveHead()
        {
            var mainCamera = UnityEngine.Camera.main;
            if (mainCamera == null) return false;
            _head = mainCamera.transform;
            return true;
        }

        private void ApplyGeometry()
        {
            if (_head == null) return;

            // Rigid head lock: copy head pose, offset forward, no smoothing.
            transform.SetPositionAndRotation(
                _head.position + _head.up * verticalOffset,
                _head.rotation);

            // Size the screen quad child only; keep rig scale identity so
            // child positions are not distorted.
            float width = height * eyeAspect;
            if (_screenRenderer != null)
                _screenRenderer.transform.localScale = new Vector3(width, height, 1f);

            PushTexture();
        }

        private void PushTexture()
        {
            if (_screenRenderer == null || videoTexture == null) return;
            _screenRenderer.GetPropertyBlock(_props);
            _props.SetTexture(MainTexId, videoTexture);
            _screenRenderer.SetPropertyBlock(_props);
        }

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.4f, 0.7f);
            float w = height * eyeAspect;
            Gizmos.DrawWireCube(transform.position, new Vector3(w, height, 0.01f));
        }
    }
}
