using System.Collections.Generic;
using PicoBridge.Immersive;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// In-app display of the live body-tracking output (bodytrack-deploy t09,
    /// reworked on the first in-headset review 2026-09-05): one white cube per
    /// hand, driven by the corrected poses the robot receives
    /// (BodyFrameCache). In Held mode (correction off) the cubes show the raw
    /// output; in Gloves mode (correction on) the adjusted one — the mode
    /// toggle doubles as a raw-vs-adjusted comparison, and the steppers move
    /// the cubes directly (yaw twists about the cube's vertical axis, level
    /// slides along it). The full skeleton was tried and dropped in that
    /// review; only the hand cubes remain, so the invisible joint hierarchy
    /// below exists purely to compose the hand poses from parent-local
    /// rotations. Grey ghost when the stream is stale; hidden while the FPV
    /// immersive screen owns the view.
    /// </summary>
    public class BodyTrackingVisualizer : MonoBehaviour
    {
        private static readonly Color CubeColor = new Color(0.95f, 0.96f, 0.98f, 0.95f);
        private static readonly Color GhostColor = new Color(0.4f, 0.4f, 0.44f, 0.75f);

        private const float StaleSeconds = 0.35f;
        private const float RigDistance = 1.6f;
        private const float CubeSize = 0.06f;

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
        private Transform _leftCube;
        private Transform _rightCube;
        private Renderer[] _cubeRenderers;

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

            // Invisible chain: parent-local poses must compose to get the hand
            // world poses; nothing between root and hands is rendered.
            _joints = new Transform[BodyFrameCache.JointCount];
            for (int i = 0; i < BodyFrameCache.JointCount; i++)
                _joints[i] = new GameObject("Joint" + i).transform;
            for (int i = 0; i < BodyFrameCache.JointCount; i++)
                _joints[i].SetParent(i == 0 ? _content : _joints[ParentOf[i]], false);

            var renderers = new List<Renderer>();
            _leftCube = MakeCube("HandCubeL", renderers);
            _leftCube.SetParent(_content, false);
            _rightCube = MakeCube("HandCubeR", renderers);
            _rightCube.SetParent(_content, false);
            _cubeRenderers = renderers.ToArray();
        }

        private static Transform MakeCube(string name, List<Renderer> renderers)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            var collider = cube.GetComponent<Collider>();
            if (Application.isPlaying)
                Destroy(collider);
            else
                DestroyImmediate(collider);
            cube.transform.localScale = Vector3.one * CubeSize;

            var shader = Shader.Find("Standard");
            var material = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            material.color = CubeColor;
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", CubeColor * 0.35f);
            }
            cube.GetComponent<Renderer>().sharedMaterial = material;
            renderers.Add(cube.GetComponent<Renderer>());
            return cube.transform;
        }

        private void Update()
        {
            // Hidden while the FPV immersive screen owns the view.
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

            // Head-follow placement: the cubes float ahead of the user at 1:1.
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

            _leftCube.SetPositionAndRotation(_joints[22].position, _joints[22].rotation);
            _rightCube.SetPositionAndRotation(_joints[23].position, _joints[23].rotation);

            // Grey ghost when the stream is stale; otherwise the corrected
            // (Gloves) or raw (Held) output white.
            bool live = Time.realtimeSinceStartup - BodyFrameCache.LastUpdate <= StaleSeconds;
            foreach (var renderer in _cubeRenderers)
                renderer.sharedMaterial.color = live ? CubeColor : GhostColor;
        }

        private void SetContentActive(bool active)
        {
            if (_content != null && _content.gameObject.activeSelf != active)
                _content.gameObject.SetActive(active);
        }
    }
}
