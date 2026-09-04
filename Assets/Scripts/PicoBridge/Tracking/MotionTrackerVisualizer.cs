using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// In-headset calibration aid for the motion trackers (mocap map t07 addendum).
    ///
    /// Renders per side, at the tracker pose: a small cube plus an RGB axis
    /// gizmo whose axes are exactly the ``pico_tracker_local`` frame — the
    /// same flipped frame (−Z/−Qz/−Qw, t01 §4) that
    /// ``tracker_synth_config.tracker_offset`` is written in — so an offset
    /// measured against the gizmo on screen pastes straight into the YAML:
    /// ``offset = (tracker_pos − wrist_pos) expressed in gizmo axes``.
    ///
    /// Valid tracking paints the cube in the side color; lost tracking keeps
    /// the last pose as a gray ghost (the operator sees where the tracker
    /// left optical view — the subjective-round FOV blocker made visible).
    /// Disconnected sides are hidden. The panel SN row mirrors optical state
    /// via <see cref="MotionTrackerBinding.SetOpticalSample"/> ("?" suffix).
    /// </summary>
    public class MotionTrackerVisualizer : MonoBehaviour
    {
        private const float SampleHz = 30f;

        private static readonly Color LeftColor = new Color(1.0f, 0.55f, 0.15f);
        private static readonly Color RightColor = new Color(0.15f, 0.8f, 0.55f);
        private static readonly Color GhostColor = new Color(0.4f, 0.4f, 0.44f);
        private static readonly Color[] AxisColors =
        {
            new Color(0.95f, 0.2f, 0.2f),   // +X red
            new Color(0.25f, 0.95f, 0.3f),  // +Y green
            new Color(0.25f, 0.45f, 1.0f),  // +Z blue
        };

        [SerializeField] private float cubeSize = 0.03f;
        [SerializeField] private float axisLength = 0.09f;
        [SerializeField] private float axisThickness = 0.006f;

        private class SideGizmo
        {
            public GameObject Root;
            public Renderer Cube;
            public Renderer[] Axes = new Renderer[3];
            public Material CubeMaterial;
            public Material[] AxisMaterials = new Material[3];
            public Color SideColor;
            public bool HasPose;
        }

        private SideGizmo _left;
        private SideGizmo _right;
        private float _sampleTimer;

        /// <summary>Create (or reuse) the visualizer under the manager.</summary>
        public static MotionTrackerVisualizer EnsureCreated(Transform parent)
        {
            var existing = FindObjectOfType<MotionTrackerVisualizer>();
            if (existing != null)
                return existing;

            var holder = new GameObject("MotionTrackerVisualizer");
            if (parent != null)
                holder.transform.SetParent(parent, false);
            return holder.AddComponent<MotionTrackerVisualizer>();
        }

        private void Start()
        {
            _left = BuildSide("TrackerGizmoLeft", LeftColor);
            _right = BuildSide("TrackerGizmoRight", RightColor);
        }

        private void Update()
        {
            _sampleTimer += Time.deltaTime;
            if (_sampleTimer < 1f / SampleHz)
                return;
            _sampleTimer = 0f;
            PollSide("left", _left);
            PollSide("right", _right);
        }

        private void PollSide(string side, SideGizmo gizmo)
        {
            if (!MotionTrackerBinding.TryGetConnectedSn(side, out long sn))
            {
                gizmo.Root.SetActive(false);
                MotionTrackerBinding.SetOpticalSample(side, false);
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            MotionTrackerLocation location = default;
            bool isValid = false;
            if (PXR_MotionTracking.GetMotionTrackerLocation(sn, ref location, ref isValid) != 0)
            {
                MotionTrackerBinding.SetOpticalSample(side, false);
                return;
            }

            // pico_tracker_local flip (same as AppendMotion): -Z, -Qz, -Qw.
            var p = location.pose.Position;
            var q = location.pose.Orientation;
            SetGizmoPose(
                gizmo,
                new Vector3(p.x, p.y, -p.z),
                new Quaternion(q.x, q.y, -q.z, -q.w),
                isValid);
            MotionTrackerBinding.SetOpticalSample(side, isValid);
#endif
        }

        /// <summary>Update pose when valid; keep the last pose as a ghost when lost.</summary>
        private void SetGizmoPose(SideGizmo g, Vector3 position, Quaternion rotation, bool valid)
        {
            if (valid || !g.HasPose)
            {
                g.Root.transform.SetPositionAndRotation(position, rotation);
                g.HasPose = true;
            }
            if (!g.Root.activeSelf)
                g.Root.SetActive(true);
            ApplyGizmoState(g, valid);
        }

        // ── construction ─────────────────────────────────────

        private SideGizmo BuildSide(string name, Color sideColor)
        {
            var root = new GameObject(name);
            root.transform.SetParent(transform, false);

            var gizmo = new SideGizmo { Root = root, SideColor = sideColor };

            gizmo.Cube = CreatePrimitiveCube(root.transform, "cube", Vector3.zero,
                Quaternion.identity, Vector3.one * cubeSize);
            gizmo.CubeMaterial = NewLitMaterial(sideColor);
            gizmo.Cube.sharedMaterial = gizmo.CubeMaterial;

            // Axis bars: thin cubes along local +X/+Y/+Z with a fatter tip at
            // the positive end so direction is readable at a glance.
            for (int axis = 0; axis < 3; axis++)
            {
                var direction = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                var bar = CreatePrimitiveCube(root.transform, $"axis{axis}",
                    direction * (axisLength * 0.5f),
                    Quaternion.FromToRotation(Vector3.forward, direction),
                    new Vector3(axisThickness, axisThickness, axisLength));
                var tip = CreatePrimitiveCube(root.transform, $"tip{axis}",
                    direction * axisLength,
                    Quaternion.FromToRotation(Vector3.forward, direction),
                    Vector3.one * (axisThickness * 2.2f));

                gizmo.AxisMaterials[axis] = NewLitMaterial(AxisColors[axis]);
                gizmo.Axes[axis] = bar;
                bar.sharedMaterial = gizmo.AxisMaterials[axis];
                tip.sharedMaterial = gizmo.AxisMaterials[axis];
            }

            root.SetActive(false);
            return gizmo;
        }

        private static Renderer CreatePrimitiveCube(Transform parent, string name, Vector3 localPosition, Quaternion localRotation, Vector3 scale)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            var collider = cube.GetComponent<Collider>();
            if (Application.isPlaying)
                Object.Destroy(collider);
            else
                Object.DestroyImmediate(collider);
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = localPosition;
            cube.transform.localRotation = localRotation;
            cube.transform.localScale = scale;
            return cube.GetComponent<Renderer>();
        }

        private static Material NewLitMaterial(Color color)
        {
            var shader = Shader.Find("Standard");
            var material = new Material(shader != null ? shader : Shader.Find("Diffuse"));
            material.color = color;
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 0.4f);
            }
            return material;
        }

        private void ApplyGizmoState(SideGizmo g, bool valid)
        {
            g.CubeMaterial.color = valid ? g.SideColor : GhostColor;
            if (g.CubeMaterial.HasProperty("_EmissionColor"))
                g.CubeMaterial.SetColor("_EmissionColor", (valid ? g.SideColor : GhostColor) * (valid ? 0.4f : 0.1f));
            for (int axis = 0; axis < 3; axis++)
            {
                var dim = valid ? AxisColors[axis] : AxisColors[axis] * 0.3f;
                g.AxisMaterials[axis].color = dim;
                if (g.AxisMaterials[axis].HasProperty("_EmissionColor"))
                    g.AxisMaterials[axis].SetColor("_EmissionColor", dim * 0.4f);
            }
        }
    }
}
