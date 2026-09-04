using PicoBridge.Camera;
using UnityEngine;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Runtime self-assembly of the stereo immersive rig (same pattern as
    /// PicoBridgeSceneSetup / PicoBridgeUI): builds the head-locked quad with
    /// the stereo SBS shader, wires the controller to the WebRTC receiver,
    /// and hides itself until entered.
    /// </summary>
    [DisallowMultipleComponent]
    public class StereoImmersiveBootstrap : MonoBehaviour
    {
        [Tooltip("Quad distance from the eyes (metres).")]
        [SerializeField] private float distance = 2.0f;
        [Tooltip("Quad height (metres); width = height * per-eye aspect.")]
        [SerializeField] private float height = 1.66f;
        [Tooltip("Per-eye aspect (SBS 2560x720 -> each eye 1280x720 = 16/9).")]
        [SerializeField] private float eyeAspect = 16f / 9f;
        [Tooltip("Panel root hidden while immersive (canvas or prefab name).")]
        [SerializeField] private string panelRootName = "[Building Block] Controller Canvas Interaction Canvas";

        private void Start()
        {
            // Quad child: forward-facing plane, shader does the per-eye half sampling.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "StereoScreen";
            quad.transform.SetParent(transform, false);
            quad.transform.localPosition = new Vector3(0f, 0f, distance);
            quad.transform.localRotation = Quaternion.identity;
            Destroy(quad.GetComponent<Collider>());

            var renderer = quad.GetComponent<Renderer>();
            // Resources folder: always included in builds, loadable by name.
            var shader = Shader.Find("PicoBridge/StereoSbsQuad");
            if (shader == null)
                shader = Resources.Load<Shader>("StereoSbsQuad");
            if (shader == null)
            {
                Debug.LogError("[StereoImmersive] StereoSbsQuad shader not found");
                Destroy(gameObject);
                return;
            }
            var material = new Material(shader);
            // Unity.WebRTC receive textures arrive top-down already; no flip.
            material.DisableKeyword("_FLIP_Y");
            renderer.sharedMaterial = material;

            var rig = gameObject.AddComponent<StereoImmersiveRig>();
            rig.Configure(distance, height, eyeAspect);

            var controller = gameObject.AddComponent<StereoImmersiveController>();
            // Loading spinner in front of the screen until the first frame,
            // plus a short-lived exit hint when immersive mode starts.
            var spinner = gameObject.AddComponent<StereoImmersiveLoadingSpinner>();
            spinner.Build(transform, distance);
            controller.SetLoadingSpinner(spinner);
            var exitHint = gameObject.AddComponent<StereoImmersiveExitHint>();
            exitHint.Build(transform, distance);
            controller.SetExitHint(exitHint);
            var receiver = FindObjectOfType<WebRtcCameraReceiver>();
            if (receiver != null)
                controller.SetWebRtcCamera(receiver);
            var teleimager = FindObjectOfType<WebRtcHttpSignalingClient>();
            if (teleimager != null)
                controller.SetTeleimagerStream(teleimager);

            // Hide the panel canvas while immersive (panel prefab name from template).
            var panel = GameObject.Find(panelRootName);
            if (panel == null)
            {
                // Fall back to the world-space canvas holding the panel.
                var canvas = FindObjectOfType<UnityEngine.Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
                    panel = canvas.gameObject;
            }
            if (panel != null)
                controller.SetPanelRoot(panel);

            // Start hidden (controller disables rig + quad on Start).
        }
    }
}
