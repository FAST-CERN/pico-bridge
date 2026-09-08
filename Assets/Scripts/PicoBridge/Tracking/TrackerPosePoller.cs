using System;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>SN lookup for one side: true when bound and connected.</summary>
    public delegate bool SnProvider(string side, out long sn);

    /// <summary>
    /// Raw PXR pose fetch for one SN. Returns false on API failure. On
    /// success fills the RAW (unflipped) tracker pose plus its optical
    /// validity flag.
    /// </summary>
    public delegate bool RawPoseProvider(long sn, out Vector3 p, out Quaternion q, out bool valid);

    /// <summary>
    /// RED stub (tracker-ik map t01): the single acquisition authority for
    /// tracker poses. Polls both sides at tracking cadence on device and
    /// publishes flipped samples into <see cref="TrackerFrameCache"/>,
    /// replacing the wire serializer's and visualizer's private polls. SN and
    /// pose sources are injectable so editor smokes drive it without the SDK;
    /// the device defaults bind MotionTrackerBinding + PXR_MotionTracking.
    /// Also owns the slow (1 Hz) session-status tick: auto-guidance once per
    /// session when the tracker flow is live but no side is valid.
    /// </summary>
    public class TrackerPosePoller : MonoBehaviour
    {
        [SerializeField] private float pollHz = 72f;
        [SerializeField] private float statusHz = 1f;

        public SnProvider SnSource;
        public RawPoseProvider PoseSource;

        private float _pollTimer;
        private float _statusTimer;

        /// <summary>Create (or reuse) the poller under the manager, device sources wired.</summary>
        public static TrackerPosePoller EnsureCreated(Transform parent)
        {
            var existing = FindObjectOfType<TrackerPosePoller>();
            if (existing != null)
                return existing;

            var holder = new GameObject("TrackerPosePoller");
            if (parent != null)
                holder.transform.SetParent(parent, false);
            var poller = holder.AddComponent<TrackerPosePoller>();
            poller.WireDeviceSources();
            return poller;
        }

        /// <summary>Device acquisition path (no-op sources in the editor).</summary>
        public void WireDeviceSources()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            SnSource = MotionTrackerBinding.TryGetConnectedSn;
            PoseSource = DevicePose;
#endif
        }

        /// <summary>Poll both sides once; test entry and the rate-limited Update.</summary>
        public void ManualPoll(float now)
        {
            PollSide("left", now);
            PollSide("right", now);
        }

        private void PollSide(string side, float now)
        {
            if (SnSource == null || PoseSource == null)
                return;

            if (!SnSource(side, out long sn))
            {
                // Unbound or disconnected: publish nothing — the cache entry
                // goes stale and the wire omits the side; the gizmo hides.
                MotionTrackerBinding.SetOpticalSample(side, false);
                return;
            }

            if (!PoseSource(sn, out Vector3 p, out Quaternion q, out bool valid))
            {
                // API failure reads as optically invalid (the pre-t01
                // visualizer treated it the same way).
                TrackerFrameCache.PublishInvalid(side, sn, now);
                MotionTrackerBinding.SetOpticalSample(side, false);
                return;
            }

            if (valid)
            {
                // pico_tracker_local flip: position -Z (validated on device
                // by the t05 Kabsch round, 0.071 m residual). The matching
                // ORIENTATION conversion under the same Z-mirror is
                // R_U = M R M^-1: mirror the vector part, KEEP the scalar —
                // (Qx, Qy, -Qz, Qw). The retired inline path (and the t01
                // golden copied from it) also negated Qw, which stores the
                // INVERSE rotation — positions stayed rigid-consistent but
                // every orientation residual exploded to mirror scale
                // (2026-09-08 round: 118 deg rms reject with correct poses).
                TrackerFrameCache.PublishValid(
                    side, sn,
                    new Vector3(p.x, p.y, -p.z),
                    new Quaternion(q.x, q.y, -q.z, q.w),
                    now);
            }
            else
            {
                TrackerFrameCache.PublishInvalid(side, sn, now);
            }
            MotionTrackerBinding.SetOpticalSample(side, valid);
        }

        private static bool DevicePose(long sn, out Vector3 p, out Quaternion q, out bool valid)
        {
            MotionTrackerLocation location = default;
            bool isValidPose = false;
            if (PXR_MotionTracking.GetMotionTrackerLocation(sn, ref location, ref isValidPose) != 0)
            {
                p = default;
                q = default;
                valid = false;
                return false;
            }

            var pp = location.pose.Position;
            var pq = location.pose.Orientation;
            p = new Vector3(pp.x, pp.y, pp.z);
            q = new Quaternion(pq.x, pq.y, pq.z, pq.w);
            valid = isValidPose;
            return true;
        }

        private void Update()
        {
            _pollTimer += Time.deltaTime;
            if (_pollTimer >= 1f / Mathf.Max(1f, pollHz))
            {
                _pollTimer = 0f;
                ManualPoll(Time.unscaledTime);
            }

            _statusTimer += Time.deltaTime;
            if (_statusTimer >= 1f / Mathf.Max(0.1f, statusHz))
            {
                _statusTimer = 0f;
                StatusTick();
            }
        }

        private void StatusTick()
        {
            var report = TrackerSessionStatus.Evaluate();
            if (report.ShouldPromptGuidance)
                TrackerSessionStatus.RequestGuidance();
        }
    }
}
