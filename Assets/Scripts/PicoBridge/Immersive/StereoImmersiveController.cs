using PicoBridge.Camera;
using PicoBridge.Tracking;
using UnityEngine;
using UnityEngine.XR;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Entry/exit logic for the stereo immersive FPV mode: toggles the
    /// StereoImmersiveRig on, feeds it the WebRTC SBS texture, and hides the
    /// bridge panel while active. Enter from the panel button, exit with
    /// either controller grip button.
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

            // Exit: controller grip button.
            var hand = InputDevices.GetDeviceAtXRNode(
                exitHand == InputDeviceCharacteristics.Left ? XRNode.LeftHand : XRNode.RightHand);
            if (hand.isValid &&
                hand.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip)
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
