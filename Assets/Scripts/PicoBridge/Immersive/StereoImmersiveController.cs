using PicoBridge.Camera;
using PicoBridge.Tracking;
using UnityEngine;
using UnityEngine.XR;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Entry/exit logic for the stereo immersive FPV mode: toggles the
    /// StereoImmersiveRig on, feeds it the WebRTC SBS texture, and hides the
    /// bridge panel while active. Enter from the panel button; exit by
    /// holding the grip for ~exitHoldSeconds — the hold must START while
    /// immersive, so a strap pressing the grip (deploy-map hand-back mount)
    /// can neither block entry nor instantly exit.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(StereoImmersiveRig))]
    public class StereoImmersiveController : MonoBehaviour
    {
        [Tooltip("Rig driven by this controller (auto-resolved on this GameObject).")]
        [SerializeField] private StereoImmersiveRig rig;
        [Tooltip("Panel root to hide while immersive (PicoBridgePanel canvas root).")]
        [SerializeField] private GameObject panelRoot;
        [Tooltip("WebRTC camera receiver supplying the SBS texture (auto-resolved).")]
        [SerializeField] private WebRtcCameraReceiver webRtcCamera;
        [Tooltip("Direct teleimager stream (HTTP signaling); preferred source when it has frames.")]
        [SerializeField] private WebRtcHttpSignalingClient teleimagerStream;
        [Tooltip("Spinner overlay shown while no video texture has arrived (built by the bootstrap).")]
        [SerializeField] private StereoImmersiveLoadingSpinner loadingSpinner;
        [Tooltip("Short-lived exit hint shown when immersive mode starts (built by the bootstrap).")]
        [SerializeField] private StereoImmersiveExitHint exitHint;
        [Tooltip("Hide panel while immersive mode is active.")]
        [SerializeField] private bool hidePanel = true;
        [Tooltip("Grip button that exits immersive mode.")]
        [SerializeField] private InputDeviceCharacteristics exitHand = InputDeviceCharacteristics.Right;
        [Tooltip("How long the exit grip must be held (seconds) after a fresh press to exit.")]
        [SerializeField] private float exitHoldSeconds = 0.8f;

        // Exit-gesture state: a grip already down at entry (strapped to the
        // hand back, the deploy-map mount, the binding presses it) must not
        // read as an exit press — only a press that STARTS while immersive,
        // held continuously for exitHoldSeconds, exits. Flicker from strap
        // pressure changes stays under the sustain window.
        private bool _gripWasDown;
        private float _gripDownSince = -1f;

        public bool IsImmersiveActive { get; private set; }

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private void Awake()
        {
            if (rig == null) rig = GetComponent<StereoImmersiveRig>();
            if (webRtcCamera == null) webRtcCamera = FindObjectOfType<WebRtcCameraReceiver>();
            if (teleimagerStream == null) teleimagerStream = FindObjectOfType<WebRtcHttpSignalingClient>();
        }

        private void Start()
        {
            // Rig starts disabled; this controller stays alive to listen for entry.
            SetImmersive(false);
        }

        private void Update()
        {
            if (!IsImmersiveActive) return;

            // Exit: a grip press that starts while immersive and is held for
            // exitHoldSeconds (edge + sustain, see the fields above).
            var hand = InputDevices.GetDeviceAtXRNode(
                exitHand == InputDeviceCharacteristics.Left ? XRNode.LeftHand : XRNode.RightHand);
            bool gripDown = hand.isValid &&
                hand.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip;
            if (ShouldExitOnGrip(gripDown, Time.realtimeSinceStartup))
            {
                SetImmersive(false);
                return;
            }

            // Keep the rig fed: the direct teleimager stream wins when it has
            // frames; the PC-push receiver (SBS test pattern or PC camera) is
            // the fallback. Until any texture exists the loading spinner
            // spins in front of the screen.
            var texture = ResolveVideoTexture();
            if (texture != null)
                rig.SetVideoTexture(texture);
            if (loadingSpinner != null)
                loadingSpinner.Visible = texture == null;
        }

        /// <summary>Edge + sustain filter for the exit grip. Returns true when a
        /// grip press that started while immersive has been held long enough.</summary>
        private bool ShouldExitOnGrip(bool gripDown, float now)
        {
            if (gripDown && !_gripWasDown)
                _gripDownSince = now;
            else if (!gripDown)
                _gripDownSince = -1f;
            _gripWasDown = gripDown;
            return _gripDownSince > 0f && now - _gripDownSince >= exitHoldSeconds;
        }

        /// <summary>Panel button entry point (also usable from code).</summary>
        public void EnterImmersive() => SetImmersive(true);

        public void ExitImmersive() => SetImmersive(false);

        public void SetPanelRoot(GameObject root) => panelRoot = root;

        public void SetWebRtcCamera(WebRtcCameraReceiver receiver) => webRtcCamera = receiver;

        public void SetTeleimagerStream(WebRtcHttpSignalingClient stream) => teleimagerStream = stream;

        public void SetLoadingSpinner(StereoImmersiveLoadingSpinner spinner) => loadingSpinner = spinner;

        public void SetExitHint(StereoImmersiveExitHint hint) => exitHint = hint;

        private UnityEngine.Texture ResolveVideoTexture()
        {
            if (teleimagerStream != null && teleimagerStream.HasVideoSignal)
                return teleimagerStream.Texture;
            if (webRtcCamera != null)
                return webRtcCamera.Texture;
            return null;
        }

        private void SetImmersive(bool active)
        {
            IsImmersiveActive = active;

            // Swallow a grip that is already down at entry (strapped to the
            // hand back it is held permanently): no exit until it is released
            // once and pressed again for exitHoldSeconds.
            _gripWasDown = true;
            _gripDownSince = -1f;
            rig.enabled = active;
            foreach (Transform child in transform)
                child.gameObject.SetActive(active);

            if (hidePanel && panelRoot != null)
                panelRoot.SetActive(!active);

            if (loadingSpinner != null)
                loadingSpinner.Visible = false;

            if (active)
            {
                // Direct stream: one HTTP POST handshake per entry (the
                // server builds a fresh peer connection per POST).
                if (teleimagerStream != null && teleimagerStream.IsConfigured)
                    teleimagerStream.StartStream();

                if (exitHint != null)
                    exitHint.Show();

                var tex = ResolveVideoTexture();
                var cam = UnityEngine.Camera.main;
                Debug.Log($"[StereoImmersive] enter: tex={tex} ({(tex != null ? tex.width + "x" + tex.height : "null")}) " +
                          $"cam={cam} quadActive={transform.GetChild(0).gameObject.activeSelf} " +
                          $"pos={transform.GetChild(0).position} scale={transform.GetChild(0).lossyScale}");
            }
            else
            {
                if (teleimagerStream != null)
                    teleimagerStream.StopStream();
            }
        }
    }
}
