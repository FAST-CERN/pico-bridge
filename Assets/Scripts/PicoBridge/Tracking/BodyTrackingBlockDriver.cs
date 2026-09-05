using System.Collections.Generic;
using PicoBridge.Immersive;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Drives the PICO SDK's own BodyTracking Building-Block avatar (the
    /// familiar white-cube full-body figure) from the corrected output cache
    /// (bodytrack-deploy t09, reworked on the 2026-09-05 reviews: no
    /// hand-built skeleton/cubes — the operator calibrates against the
    /// original SDK avatar). The block's own Update stays inert (its
    /// StartBodyTracking is never called), so this driver is the only writer:
    /// what the avatar shows is exactly what the robot receives — raw in Held
    /// mode, adjusted in Gloves mode — and the steppers move it directly.
    /// </summary>
    public class BodyTrackingBlockDriver : MonoBehaviour
    {
        private const float RigDistance = 1.6f;

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

        private void Update()
        {
            // Hidden while the FPV immersive screen owns the view.
            var immersive = FindObjectOfType<StereoImmersiveController>();
            bool hidden = immersive != null && immersive.IsImmersiveActive;
            if (_root != null && _root.gameObject.activeSelf != !hidden && BodyFrameCache.HasData)
                _root.gameObject.SetActive(!hidden);
            if (hidden || !BodyFrameCache.HasData)
                return;

            // Head-follow placement: the avatar floats ahead of the user at
            // 1:1 so the hands sit next to the real ones.
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

            // Role 0 (pelvis) has no avatar node in the prefab; the chain
            // composes from the spine down to the hand cubes regardless.
            for (int i = 1; i < BodyFrameCache.JointCount; i++)
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
