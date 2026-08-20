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

            // Keep the rig fed with the latest WebRTC texture.
            if (webRtcCamera != null && webRtcCamera.Texture != null)
                rig.SetVideoTexture(webRtcCamera.Texture);
        }

        /// <summary>Panel button entry point (also usable from code).</summary>
        public void EnterImmersive() => SetImmersive(true);

        public void ExitImmersive() => SetImmersive(false);

        public void SetPanelRoot(GameObject root) => panelRoot = root;

        public void SetWebRtcCamera(WebRtcCameraReceiver receiver) => webRtcCamera = receiver;

        private void SetImmersive(bool active)
        {
            IsImmersiveActive = active;
            rig.enabled = active;
            foreach (Transform child in transform)
                child.gameObject.SetActive(active);

            if (hidePanel && panelRoot != null)
                panelRoot.SetActive(!active);

            if (active)
            {
                var tex = webRtcCamera != null ? webRtcCamera.Texture : null;
                var cam = UnityEngine.Camera.main;
                Debug.Log($"[StereoImmersive] enter: tex={tex} ({(tex != null ? tex.width + "x" + tex.height : "null")}) " +
                          $"cam={cam} quadActive={transform.GetChild(0).gameObject.activeSelf} " +
                          $"pos={transform.GetChild(0).position} scale={transform.GetChild(0).lossyScale}");
            }
        }
    }
}
