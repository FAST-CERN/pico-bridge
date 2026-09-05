using System.Collections.Generic;
using PicoBridge.Immersive;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// In-app rendering of the 24-joint body-tracking skeleton, corrected data
    /// included (bodytrack-deploy t09). The operator watches in-headset what
    /// the robot receives: per-side hand blocks are the calibration targets
    /// (visual language shared with the tracker gizmos — left orange, right
    /// green; grey ghost when the stream is stale; hidden while the FPV
    /// immersive screen owns the view).
    ///
    /// Local poses drive a 24-joint transform hierarchy rebuilt from
    /// BodyFrameCache each frame; bones are stretched cubes between joints.
    /// The rig floats ~1.6 m in front of the head (yaw-follow) at 1:1 scale.
    /// The content container (not this component's GameObject) toggles
    /// visibility so Update keeps running while hidden.
    /// </summary>
    public class BodyTrackingVisualizer : MonoBehaviour
    {
        private static readonly Color LeftColor = new Color(1.0f, 0.55f, 0.15f);
        private static readonly Color RightColor = new Color(0.15f, 0.8f, 0.55f);
        private static readonly Color BoneColor = new Color(0.85f, 0.87f, 0.9f, 0.65f);
        private static readonly Color GhostColor = new Color(0.4f, 0.4f, 0.44f, 0.75f);

        private const float StaleSeconds = 0.35f;
        private const float RigDistance = 1.6f;
        private const float HandBlockSize = 0.06f;
        private const float WristMarkerSize = 0.028f;
        private const float BoneThickness = 0.012f;

        // BodyTrackerRole order (PXR_Plugin): Pelvis=0 .. RIGHT_HAND=23.
        // Entry i = parent joint of role i (-1 = root).
        private static readonly int[] ParentOf =
        {
            -1,        // 0 Pelvis (root)
            0, 0, 0,   // 1 L_HIP, 2 R_HIP, 3 SPINE1
            1, 2,      // 4 L_KNEE, 5 R_KNEE
            3,         // 6 SPINE2
            4, 5,      // 7 L_ANKLE, 8 R_ANKLE
            6,         // 9 SPINE3
            7, 8,      // 10 L_FOOT, 11 R_FOOT
            9, 9, 9,   // 12 NECK, 13 L_COLLAR, 14 R_COLLAR
            12,        // 15 HEAD
            13, 14,    // 16 L_SHOULDER, 17 R_SHOULDER
            16, 17,    // 18 L_ELBOW, 19 R_ELBOW
            18, 19,    // 20 L_WRIST, 21 R_WRIST
            20, 21,    // 22 L_HAND, 23 R_HAND
        };

        private Transform _content;
        private Transform[] _joints;
        private Transform[] _bones;
        private Transform _leftHand;
        private Transform _rightHand;
        private Renderer[] _renderers;
        private Color[] _baseColors;

        private readonly Vector3[] _positions = new Vector3[BodyFrameCache.JointCount];
        private readonly Quaternion[] _rotations = new Quaternion[BodyFrameCache.JointCount];

        public static BodyTrackingVisualizer EnsureCreated(Transform parent)
        {
            var existing = parent.GetComponentInChildren<BodyTrackingVisualizer>(true);
            if (existing != null)
                return existing;

            var rootObject = new GameObject("BodyTrackingViz");
            rootObject.transform.SetParent(parent, false);
            return rootObject.AddComponent<BodyTrackingVisualizer>();
        }

        private void Start()
        {
            BuildRig();
            _content.gameObject.SetActive(false); // until the first cached frame
        }

        private void BuildRig()
        {
            _content = new GameObject("VizContent").transform;
            _content.SetParent(transform, false);

            _joints = new Transform[BodyFrameCache.JointCount];
            for (int i = 0; i < BodyFrameCache.JointCount; i++)
            {
                var jointObject = new GameObject("Joint" + i);
                _joints[i] = jointObject.transform;
            }
            // Chain per ParentOf: cached local poses are parent-relative, so
            // the hierarchy must mirror the skeleton for them to compose.
            for (int i = 0; i < BodyFrameCache.JointCount; i++)
                _joints[i].SetParent(i == 0 ? _content : _joints[ParentOf[i]], false);

            var rendererList = new List<Renderer>();
            var colorList = new List<Color>();

            foreach (int wrist in new[] { 20, 21 })
            {
                var marker = MakeBlock("WristMarker" + wrist, BoneColor, WristMarkerSize, rendererList, colorList);
                marker.SetParent(_joints[wrist], false);
            }

            _bones = new Transform[BodyFrameCache.JointCount - 1];
            int boneIndex = 0;
            for (int i = 1; i < BodyFrameCache.JointCount; i++)
            {
                var bone = MakeBlock("Bone" + i, BoneColor, BoneThickness, rendererList, colorList);
                bone.SetParent(_content, false);
                _bones[boneIndex++] = bone;
            }

            _leftHand = MakeBlock("HandBlockL", LeftColor, HandBlockSize, rendererList, colorList);
            _leftHand.SetParent(_content, false);
            _rightHand = MakeBlock("HandBlockR", RightColor, HandBlockSize, rendererList, colorList);
            _rightHand.SetParent(_content, false);

            _renderers = rendererList.ToArray();
            _baseColors = colorList.ToArray();
        }

        private static Transform MakeBlock(string name, Color color, float size, List<Renderer> renderers, List<Color> colors)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            var collider = block.GetComponent<Collider>();
            if (Application.isPlaying)
                Destroy(collider);
            else
                DestroyImmediate(collider);
            block.transform.localScale = new Vector3(size, size, size);

            var shader = Shader.Find("Standard");
            var material = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            material.color = color;
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 0.4f);
            }
            block.GetComponent<Renderer>().sharedMaterial = material;

            renderers.Add(block.GetComponent<Renderer>());
            colors.Add(color);
            return block.transform;
        }

        private void Update()
        {
            // Hidden while the FPV immersive screen owns the view (the panel
            // hides for the same reason).
            var immersive = FindObjectOfType<StereoImmersiveController>();
            if (immersive != null && immersive.IsImmersiveActive)
            {
                SetContentActive(false);
                return;
            }

            if (!BodyFrameCache.HasData)
            {
                SetContentActive(false);
                return;
            }

            SetContentActive(true);

            // Head-follow placement: 1:1 skeleton floating ahead of the user.
            // (Fully qualified: PicoBridge.Camera shadows UnityEngine.Camera.)
            var cam = UnityEngine.Camera.main;
            if (cam != null)
            {
                Vector3 forward = cam.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f)
                    forward = Vector3.forward;
                transform.position = cam.transform.position + forward.normalized * RigDistance;
                transform.rotation = Quaternion.LookRotation(-forward.normalized, Vector3.up);
            }

            if (!BodyFrameCache.TryGetFrame(_positions, _rotations))
                return;

            for (int i = 0; i < BodyFrameCache.JointCount; i++)
            {
                _joints[i].localPosition = _positions[i];
                _joints[i].localRotation = _rotations[i];
            }

            int boneIndex = 0;
            for (int i = 1; i < BodyFrameCache.JointCount; i++)
            {
                Vector3 from = _joints[ParentOf[i]].position;
                Vector3 to = _joints[i].position;
                var bone = _bones[boneIndex++];
                Vector3 delta = to - from;
                float length = delta.magnitude;
                bone.gameObject.SetActive(length > 1e-5f);
                if (length > 1e-5f)
                {
                    bone.position = (from + to) * 0.5f;
                    bone.rotation = Quaternion.LookRotation(delta);
                    bone.localScale = new Vector3(BoneThickness, BoneThickness, length);
                }
            }

            // Hand blocks sit on the HAND joints (22/23), scaled up so the
            // calibration target is unmistakable; they move the moment a
            // stepper or set_mount_correction changes the store.
            _leftHand.SetPositionAndRotation(_joints[22].position, _joints[22].rotation);
            _rightHand.SetPositionAndRotation(_joints[23].position, _joints[23].rotation);

            // Grey ghost when the stream is stale (tracker-gizmo language):
            // keep the last pose, mute every color.
            bool live = Time.realtimeSinceStartup - BodyFrameCache.LastUpdate <= StaleSeconds;
            for (int i = 0; i < _renderers.Length; i++)
            {
                var material = _renderers[i].sharedMaterial;
                material.color = live ? _baseColors[i] : GhostColor;
            }
        }

        private void SetContentActive(bool active)
        {
            if (_content != null && _content.gameObject.activeSelf != active)
                _content.gameObject.SetActive(active);
        }
    }
}
