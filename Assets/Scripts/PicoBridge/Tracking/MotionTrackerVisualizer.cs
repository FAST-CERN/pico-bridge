using System;
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
    /// Disconnected or stale sides are hidden. Since t01 the poses come from
    /// <see cref="TrackerFrameCache"/> (the poller owns acquisition and feeds
    /// the panel SN row's optical "?" state).
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
            public bool IsHand;
        }

        private SideGizmo _left;
        private SideGizmo _right;
        // t07: the mapped-hand mirrors (bright calibration verification
        // gizmos; hidden until a side's calibration is committed).
        private SideGizmo _leftHand;
        private SideGizmo _rightHand;
        private float _sampleTimer;

        /// <summary>Test seam: forces the immersive-FPV visibility rule.</summary>
        public Func<bool> ImmersiveHideOverride;

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
            _left = BuildSide("TrackerGizmoLeft", "L", LeftColor);
            _right = BuildSide("TrackerGizmoRight", "R", RightColor);
            _leftHand = BuildSide("HandGizmoLeft", "L", LeftColor, hand: true);
            _rightHand = BuildSide("HandGizmoRight", "R", RightColor, hand: true);
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
            // t01: render from the shared acquisition cache (the poller owns
            // sampling + the optical-validity feed); poses arrive already
            // flipped into pico_tracker_local. Also works in the editor when
            // tests fill the cache.
            // t02: same display rule as the body avatar — hidden while the
            // stereo immersive FPV screen owns the view.
            var hand = side == "left" ? _leftHand : _rightHand;
            if (ImmersiveHidesVisuals() ||
                !MotionTrackerBinding.TryGetConnectedSn(side, out long sn) ||
                !TrackerFrameCache.TryGetFrame(side, out var frame) || frame.Sn != sn ||
                !TrackerFrameCache.IsFresh(frame, TrackerFrameCache.Clock()) || !frame.HasPose)
            {
                gizmo.Root.SetActive(false);
                if (hand != null)
                    hand.Root.SetActive(false);
                return;
            }

            // t07: when this side carries a committed calibration, the raw
            // puck gizmo dims and the bright mapped-hand gizmo takes over
            // (TryMap in the same stage frame — zero conversion).
            bool calibrated = false;
            var handPos = Vector3.zero;
            var handRot = Quaternion.identity;
            if (hand != null)
                calibrated = TrackerHandCalibration.TryMap(
                    side, frame.Position, frame.Rotation, out handPos, out handRot);
            SetGizmoPose(gizmo, frame.Position, frame.Rotation, frame.Valid, muted: calibrated);
            if (hand != null)
            {
                if (calibrated)
                    SetGizmoPose(hand, handPos, handRot, frame.Valid, bright: true);
                else
                    hand.Root.SetActive(false);
            }
        }

        private bool ImmersiveHidesVisuals()
        {
            if (ImmersiveHideOverride != null)
                return ImmersiveHideOverride();
            var immersive = FindObjectOfType<PicoBridge.Immersive.StereoImmersiveController>();
            return immersive != null && immersive.IsImmersiveActive;
        }

        /// <summary>Update pose when valid; keep the last pose as a ghost
        /// when lost. ``muted`` dims a valid gizmo (raw puck under an active
        /// calibration); ``bright`` boosts it (the t07 hand gizmo).</summary>
        private void SetGizmoPose(SideGizmo g, Vector3 position, Quaternion rotation, bool valid,
            bool muted = false, bool bright = false)
        {
            if (valid || !g.HasPose)
            {
                g.Root.transform.SetPositionAndRotation(position, rotation);
                g.HasPose = true;
            }
            if (!g.Root.activeSelf)
                g.Root.SetActive(true);
            ApplyGizmoState(g, valid, muted, bright);
        }

        // ── construction ─────────────────────────────────────

        private SideGizmo BuildSide(string name, string sideLabel, Color sideColor, bool hand = false)
        {
            var root = new GameObject(name);
            root.transform.SetParent(transform, false);

            var gizmo = new SideGizmo { Root = root, SideColor = sideColor, IsHand = hand };

            var size = cubeSize * (hand ? 1.15f : 1f);
            gizmo.Cube = CreatePrimitiveCube(root.transform, "cube", Vector3.zero,
                Quaternion.identity, Vector3.one * size);
            gizmo.CubeMaterial = NewLitMaterial(sideColor);
            gizmo.Cube.sharedMaterial = gizmo.CubeMaterial;

            // t04 ④: L/R letters ON the cube's ±Z faces (2026-09-08 review:
            // not above the cube, where they read as part of the axis
            // gizmo), dyed with the side color — identifies the side at a
            // glance in-headset and stays side-colored on the gray ghost so
            // a lost side is still attributable. The back copy is mirrored
            // so the letter reads correctly from behind too.
            MakeSideLabel(root.transform, "sideLabel", sideLabel, sideColor, false);
            MakeSideLabel(root.transform, "sideLabelBack", sideLabel, sideColor, true);

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

        private void MakeSideLabel(Transform parent, string name, string text, Color sideColor, bool back)
        {
            var labelObject = new GameObject(name);
            labelObject.transform.SetParent(parent, false);
            // On the ±Z cube face (half cubeSize + a hair to avoid z-fight).
            labelObject.transform.localPosition = new Vector3(0f, 0f, back ? -0.016f : 0.016f);
            if (back)
                labelObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var label = labelObject.AddComponent<TextMesh>();
            label.text = text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 64;
            // ~0.02 m letter — fits the 0.03 m cube face with margin.
            label.characterSize = 0.003f;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.color = sideColor;
        }

        private static Renderer CreatePrimitiveCube(Transform parent, string name, Vector3 localPosition, Quaternion localRotation, Vector3 scale)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            var collider = cube.GetComponent<Collider>();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(collider);
            else
                UnityEngine.Object.DestroyImmediate(collider);
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

        private void ApplyGizmoState(SideGizmo g, bool valid, bool muted = false, bool bright = false)
        {
            // t07 states: valid+bright = the mapped-hand gizmo (calibration
            // verdict, strongest presence); valid = classic puck;
            // valid+muted = raw puck under an active calibration (context);
            // invalid = gray ghost either way.
            Color baseColor = valid ? (muted ? g.SideColor * 0.45f : g.SideColor) : GhostColor;
            float emission = valid ? (bright ? 0.9f : muted ? 0.15f : 0.4f) : 0.1f;
            g.CubeMaterial.color = baseColor;
            if (g.CubeMaterial.HasProperty("_EmissionColor"))
                g.CubeMaterial.SetColor("_EmissionColor", baseColor * emission);
            float axisDim = valid ? (muted ? 0.35f : 1f) : 0.3f;
            for (int axis = 0; axis < 3; axis++)
            {
                var dim = AxisColors[axis] * axisDim;
                g.AxisMaterials[axis].color = dim;
                if (g.AxisMaterials[axis].HasProperty("_EmissionColor"))
                    g.AxisMaterials[axis].SetColor("_EmissionColor", dim * (bright ? 0.9f : 0.4f));
            }
        }
    }
}
