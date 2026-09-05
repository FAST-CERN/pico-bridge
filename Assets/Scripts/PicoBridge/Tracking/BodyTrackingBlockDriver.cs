using System.Collections.Generic;
using PicoBridge.Immersive;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Drives the PICO SDK's own BodyTracking Building-Block avatar (the
    /// familiar white-cube full-body figure) from the corrected NATIVE output
    /// cache (bodytrack-deploy t09, reworked across the 2026-09-05 review
    /// rounds). The prefab's hierarchy only composes correctly with
    /// PICO-native local poses, so the collector now applies the mount
    /// correction in the native frame pre-flip and caches that; this driver
    /// assigns those locals exactly as the block itself would (original
    /// rendering position & configuration — the block's own Start places the
    /// figure, no transform manipulation here). The wire output is the same
    /// corrected pose after the standard flip, so the avatar shows precisely
    /// what the robot receives — raw in Held mode, adjusted in Gloves mode —
    /// and the steppers move it directly.
    ///
    /// Selective rendering: the HEAD (and NECK) cubes are hidden — they sit
    /// at eye height and block the view; everything else stays for the
    /// hand-back comparison.
    /// </summary>
    public class BodyTrackingBlockDriver : MonoBehaviour
    {
        private static readonly string[] HiddenRoles = { "HEAD", "NECK" };

        private readonly Transform[] _joints = new Transform[BodyFrameCache.JointCount];
        private Transform _root;

        private readonly Vector3[] _positions = new Vector3[BodyFrameCache.JointCount];
        private readonly Quaternion[] _rotations = new Quaternion[BodyFrameCache.JointCount];

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
            HideOccludingCubes();
        }

        /// <summary>Map avatar joints by BodyTrackerRole enum names (same scheme as
        /// the block's own InitializeSkeletonJoints; unparseable nodes skipped).</summary>
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
                    _joints[(int)role] = node;
            }
        }

        private void HideOccludingCubes()
        {
            foreach (var roleName in HiddenRoles)
            {
                foreach (var joint in _joints)
                {
                    if (joint == null || joint.name != roleName)
                        continue;
                    var cube = joint.Find("Cube");
                    if (cube != null)
                        cube.gameObject.SetActive(false);
                }
            }
        }

        private void Update()
        {
            // Hidden while the FPV immersive screen owns the view.
            var immersive = FindObjectOfType<StereoImmersiveController>();
            bool hidden = immersive != null && immersive.IsImmersiveActive;
            if (_root != null && _root.gameObject.activeSelf != !hidden && BodyFrameCache.HasData)
                _root.gameObject.SetActive(!hidden);
            if (hidden || !BodyFrameCache.HasData)
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
    }
}
