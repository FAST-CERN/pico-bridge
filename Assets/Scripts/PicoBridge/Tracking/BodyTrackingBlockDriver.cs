using System.Collections.Generic;
using System.IO;
using PicoBridge.Immersive;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Drives the PICO SDK's own BodyTracking Building-Block avatar (the
    /// familiar white-cube full-body figure) from the corrected NATIVE output
    /// cache (bodytrack-deploy t09, reworked across the 2025-09-05 review
    /// rounds). The prefab's hierarchy only composes correctly with
    /// PICO-native local poses, so the collector applies corrections in the
    /// native frame pre-flip and caches that; this driver assigns those
    /// locals exactly as the block itself would. The wire output is the same
    /// pose after the standard flip, so the avatar shows precisely what the
    /// robot receives — SDK body data in Body mode, the t08 IK chain in
    /// TrackerBody mode (t09 wiring).
    ///
    /// Display sets (tracker-ik map t10, grilled 2026-09-09, HEAD ruling
    /// revised on device 06:38):
    /// - Body / idle: the bodytrack-deploy t07 contract unchanged — HEAD and
    ///   NECK cubes hidden (eye-height occlusion), everything else visible.
    /// - TrackerBody: only the upper chain renders — lower body hidden,
    ///   HEAD hidden too (even at 0.4 scale it blocked the view), NECK
    ///   visible and shrunk to HeadShrinkFactor so the head-line overlap
    ///   check still has its anchor.
    /// The set follows ArmStreamMode via SetDisplayMode (ApplyArmStream);
    /// switching back restores scales and visibility exactly (zero body-mode
    /// regression).
    ///
    /// Forensics: the 09-09 05:28 round streamed correct body frames but the
    /// operator saw no avatar. Every state transition appends one line to
    /// avatar_status.jsonl beside the stores (the chatty-proof channel, t14
    /// pattern): mapped joints, cubes found, root/immersive/cache state.
    /// </summary>
    public class BodyTrackingBlockDriver : MonoBehaviour
    {
        /// <summary>HEAD/NECK cube scale in TrackerBody mode (grill ruling:
        /// shrink, no material surgery on the shared SDK cube material).</summary>
        public const float HeadShrinkFactor = 0.4f;

        private static bool _trackerBodyDisplay;

        /// <summary>Display-set switch, driven by ApplyArmStream on every
        /// ArmStreamMode change. Static so the mode matrix (which runs in
        /// editor tests too) never needs the scene instance.</summary>
        public static void SetDisplayMode(bool trackerBody)
        {
            _trackerBodyDisplay = trackerBody;
        }

        /// <summary>Cube visibility per role for the current display mode
        /// (pure table — the smoke's contract surface). Tracker mode: upper
        /// chain only, and HEAD stays hidden too (the 06:38 device round:
        /// even shrunk to 0.4 it sits at eye height and blocks the view —
        /// the neck cube below carries the head-line overlap signal).</summary>
        public static bool IsRoleVisible(int role, bool trackerBodyDisplay)
        {
            if (role < 0 || role >= (int)BodyTrackerRole.ROLE_NUM)
                return false;
            if (trackerBodyDisplay)
                return role >= (int)BodyTrackerRole.NECK && role != (int)BodyTrackerRole.HEAD;
            return role != (int)BodyTrackerRole.NECK && role != (int)BodyTrackerRole.HEAD; // t07 contract
        }

        private readonly Transform[] _joints = new Transform[BodyFrameCache.JointCount];
        private readonly Transform[] _cubes = new Transform[BodyFrameCache.JointCount];
        private readonly Vector3[] _cubeBaseScales = new Vector3[BodyFrameCache.JointCount];
        private Transform _root;

        private readonly Vector3[] _positions = new Vector3[BodyFrameCache.JointCount];
        private readonly Quaternion[] _rotations = new Quaternion[BodyFrameCache.JointCount];

        private bool _appliedTrackerBodyDisplay;
        private bool _loggedFirst;
        private bool _loggedHasData;

        public static BodyTrackingBlockDriver EnsureCreated()
        {
            var existing = FindObjectOfType<BodyTrackingBlockDriver>();
            if (existing != null)
                return existing;

            var block = FindObjectOfType<PXR_BodyTrackingBlock>();
            if (block == null || block.skeletonJoints == null)
                return null;

            return block.gameObject.AddComponent<BodyTrackingBlockDriver>();
        }

        private void Start()
        {
            var block = GetComponent<PXR_BodyTrackingBlock>();
            _root = block != null && block.skeletonJoints != null ? block.skeletonJoints : transform;
            _root.gameObject.SetActive(true);
            MapJointsByName();
            _appliedTrackerBodyDisplay = _trackerBodyDisplay;
            ApplyDisplaySet();
        }

        /// <summary>Map avatar joints by BodyTrackerRole enum names (same scheme as
        /// the block's own InitializeSkeletonJoints; unparseable nodes skipped).
        /// Also captures each cube's base scale for the shrink/restore cycle.</summary>
        private void MapJointsByName()
        {
            var queue = new Queue<Transform>();
            queue.Enqueue(_root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (Transform child in node)
                    queue.Enqueue(child);
                if (System.Enum.TryParse(node.name, out BodyTrackerRole role))
                {
                    int index = (int)role;
                    if (index < BodyFrameCache.JointCount)
                    {
                        _joints[index] = node;
                        var cube = node.Find("Cube");
                        _cubes[index] = cube;
                        if (cube != null)
                            _cubeBaseScales[index] = cube.localScale;
                    }
                }
            }
        }

        /// <summary>Apply the current display set: cube visibility per role +
        /// HEAD/NECK shrink in TrackerBody mode. Idempotent.</summary>
        private void ApplyDisplaySet()
        {
            bool trackerBody = _trackerBodyDisplay;
            for (int i = 0; i < BodyFrameCache.JointCount; i++)
            {
                var cube = _cubes[i];
                if (cube == null)
                    continue;
                bool visible = IsRoleVisible(i, trackerBody);
                if (cube.gameObject.activeSelf != visible)
                    cube.gameObject.SetActive(visible);
                bool shrink = trackerBody && i == (int)BodyTrackerRole.NECK;
                cube.localScale = shrink
                    ? _cubeBaseScales[i] * HeadShrinkFactor
                    : _cubeBaseScales[i];
            }
        }

        private void Update()
        {
            // Hidden while the FPV immersive screen owns the view.
            var immersive = FindObjectOfType<StereoImmersiveController>();
            bool hidden = immersive != null && immersive.IsImmersiveActive;
            bool hasData = BodyFrameCache.HasData;
            if (_root != null && _root.gameObject.activeSelf != !hidden && hasData)
                _root.gameObject.SetActive(!hidden);

            // Display-set switch follows the mode matrix (t10).
            if (_trackerBodyDisplay != _appliedTrackerBodyDisplay)
            {
                _appliedTrackerBodyDisplay = _trackerBodyDisplay;
                ApplyDisplaySet();
                AppendStatus("mode");
            }
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                AppendStatus("first");
            }
            if (hasData && !_loggedHasData)
            {
                _loggedHasData = true;
                AppendStatus("data");
            }

            if (hidden || !hasData)
                return;

            if (!BodyFrameCache.TryGetFrame(_positions, _rotations))
                return;

            for (int i = 0; i < BodyFrameCache.JointCount; i++)
            {
                var joint = _joints[i];
                if (joint == null)
                    continue;
                joint.localPosition = _positions[i];
                joint.localRotation = _rotations[i];
            }
        }

        /// <summary>One JSON status line per state transition — the chatty-proof
        /// forensic channel for the t10 avatar-visibility investigation (the
        /// 09-09 round: body frames streamed correctly, operator saw nothing;
        /// logcat is eaten within seconds by PxrUnityNative spam).</summary>
        private void AppendStatus(string kind)
        {
            try
            {
                int mapped = 0, cubes = 0;
                for (int i = 0; i < BodyFrameCache.JointCount; i++)
                {
                    if (_joints[i] != null) mapped++;
                    if (_cubes[i] != null) cubes++;
                }
                var immersive = FindObjectOfType<StereoImmersiveController>();
                var line =
                    "{\"kind\":\"" + kind + "\"," +
                    "\"mode\":\"" + (_trackerBodyDisplay ? "tracker" : "body") + "\"," +
                    "\"mapped\":" + mapped + "," +
                    "\"cubes\":" + cubes + "," +
                    "\"rootActive\":" + (_root != null && _root.gameObject.activeSelf ? "true" : "false") + "," +
                    "\"immersive\":" + (immersive != null && immersive.IsImmersiveActive ? "true" : "false") + "," +
                    "\"hasData\":" + (BodyFrameCache.HasData ? "true" : "false") + "," +
                    "\"t\":" + Time.realtimeSinceStartupAsDouble.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "}";
                File.AppendAllText(
                    Path.Combine(Application.persistentDataPath, "avatar_status.jsonl"),
                    line + "\n");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PicoBridge] avatar status append failed: {e.Message}");
            }
        }
    }
}
