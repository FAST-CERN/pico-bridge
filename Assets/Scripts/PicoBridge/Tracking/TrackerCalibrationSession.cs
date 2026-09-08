using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Guided three-pose calibration session (tracker-ik map t05, Q2):
    /// begin -> 3x dual-side capture -> solve -> residual gate -> commit.
    /// One session calibrates BOTH sides (each pose sampled on both pucks,
    /// each side solving its own {R,t}).
    ///
    /// Capture requires both sides' cache frames to be fresh and optically
    /// valid and the head source to answer; the target pose is composed at
    /// capture time (target = head ∘ CalibrationPoses.local), so the
    /// operator's head may move between poses. On a gate failure the session
    /// enters Rejected, clears its slots, and the operator recaptures all
    /// three poses (simple state machine over partial-slot surgery).
    /// </summary>
    public static class TrackerCalibrationSession
    {
        public enum State { Idle, Capturing, Committed, Rejected }

        /// <summary>Injectable head pose (Unity frame, same as the tracker
        /// cache); device default reads the XR head camera.</summary>
        public delegate bool HeadPoseDelegate(out Vector3 position, out Quaternion rotation);

        // Residual gates (t05 Q2; device rounds tune). 2026-09-08 rounds:
        // position 0.325 -> 0.071 m with in-app guidance, gate to 0.10
        // (mirrored/garbage explodes past 0.3 m). Rotation gate is a
        // TOTAL-GARBAGE catch only (150°): the pucks mount on the
        // thumb-base lateral faces with uncontrolled rotation, the solved R
        // comes from POSITIONS alone (literals cannot corrupt the stored
        // calibration), and the rot residual measures the pose-orientation
        // literals vs the operator's natural hand orientations — a correct
        // round measured 118° on first-pass literals, so the gate must sit
        // above that. The inverse-quat bug class lands ~90-180°.
        public const float PositionGateMeters = 0.10f;
        public const float RotationGateDegrees = 150f;

        private struct Sample
        {
            public Vector3 TargetPos;
            public Vector3 PuckPos;
            public Quaternion TargetRot;
            public Quaternion PuckRot;
        }

        private static readonly object _lock = new object();
        private static State _state = State.Idle;
        private static int _nextPose;
        private static string _rejectReason = "";
        private static Sample[] _left = new Sample[CalibrationPoses.Count];
        private static Sample[] _right = new Sample[CalibrationPoses.Count];

        public static HeadPoseDelegate HeadSource;

        public static State CurrentState
        {
            get { lock (_lock) return _state; }
        }

        public static int NextPoseIndex
        {
            get { lock (_lock) return _nextPose; }
        }

        public static string LastRejectReason
        {
            get { lock (_lock) return _rejectReason; }
        }

        public static void Begin()
        {
            lock (_lock)
            {
                ResetSlotsLocked();
                _state = State.Capturing;
                _rejectReason = "";
            }
        }

        /// <summary>Sample both sides for the current pose. Returns false
        /// (no slot filled) when either side's frame is stale/invalid or the
        /// head source is unavailable. The third capture auto-solves.
        /// Capturing after a Rejected solve doubles as the retry: slots were
        /// cleared, so recapture starts from pose 0 in the same session.</summary>
        public static bool Capture()
        {
            lock (_lock)
            {
                if (_state == State.Rejected)
                    _state = State.Capturing;
                if (_state != State.Capturing || _nextPose >= CalibrationPoses.Count)
                    return false;

                if (!TrySample("left", _nextPose, out var leftSample) ||
                    !TrySample("right", _nextPose, out var rightSample))
                    return false;

                _left[_nextPose] = leftSample;
                _right[_nextPose] = rightSample;
                _nextPose++;

                if (_nextPose >= CalibrationPoses.Count)
                    SolveLocked();
                return true;
            }
        }

        public static void Abort()
        {
            lock (_lock)
            {
                ResetSlotsLocked();
                _state = State.Idle;
                _rejectReason = "";
            }
        }

        /// <summary>Test seam: back to pristine Idle with no head source.</summary>
        public static void ResetForTest()
        {
            lock (_lock)
            {
                ResetSlotsLocked();
                _state = State.Idle;
                _rejectReason = "";
                HeadSource = null;
            }
        }

        private static bool TrySample(string side, int poseIndex, out Sample sample)
        {
            sample = default;
            if (!TrackerFrameCache.TryGetFrame(side, out var frame) ||
                !TrackerFrameCache.IsFresh(frame, TrackerFrameCache.Clock()) || !frame.Valid)
                return false;
            if (HeadSource == null || !HeadSource(out var headPos, out var headRot))
                return false;

            CalibrationPoses.GetLocalPose(poseIndex, side, out var localPos, out var localRot);
            sample.PuckPos = frame.Position;
            sample.PuckRot = frame.Rotation;
            sample.TargetPos = headPos + headRot * localPos;
            sample.TargetRot = headRot * localRot;
            return true;
        }

        private static void SolveLocked()
        {
            // Solve BOTH sides first; commit only when both pass, so a
            // one-sided failure never leaves a half-updated store.
            bool leftOk = SolveSideLocked(_left, "left", out var leftParams, out var leftReason);
            bool rightOk = SolveSideLocked(_right, "right", out var rightParams, out var rightReason);
            if (leftOk && rightOk)
            {
                TrackerHandCalibration.Commit("left", leftParams);
                TrackerHandCalibration.Commit("right", rightParams);
                _state = State.Committed;
                _rejectReason = "";
                // Round observability: residuals are the gate-tuning signal
                // (t05 device round) — logcat is the only channel while the
                // store has no reader yet.
                Debug.Log($"[PicoBridge] Hand calibration committed: " +
                          $"L pos {leftParams.positionRms * 1000f:0.0}mm rot {leftParams.rotationRmsDeg:0.0}° | " +
                          $"R pos {rightParams.positionRms * 1000f:0.0}mm rot {rightParams.rotationRmsDeg:0.0}°");
                return;
            }

            _state = State.Rejected;
            _rejectReason = !leftOk ? leftReason : rightReason;
            ResetSlotsLocked();
        }

        private static bool SolveSideLocked(
            Sample[] samples, string side,
            out TrackerHandCalibration.SideParams solved, out string reason)
        {
            solved = null;
            reason = "";

            var from = new Vector3[samples.Length];
            var to = new Vector3[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                from[i] = samples[i].PuckPos;
                to[i] = samples[i].TargetPos;
            }

            if (!KabschSolver.Solve(from, to, out var rot, out var translation, out var posRms))
            {
                reason = side + ": degenerate point spread";
                return false;
            }

            // Orientation residual under the SAME R: the puck is rigidly
            // strapped, so hand_rot = R * puck_rot must match the target.
            // This is also where reflection-flavoured data explodes — point
            // positions alone cannot see chirality (triangle congruence).
            float rotSumSq = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float angle = Quaternion.Angle(rot * samples[i].PuckRot, samples[i].TargetRot);
                rotSumSq += angle * angle;
            }
            float rotRms = Mathf.Sqrt(rotSumSq / samples.Length);

            if (posRms > PositionGateMeters)
            {
                reason = $"{side}: position rms {posRms:0.000} m over gate {PositionGateMeters:0.000}";
                return false;
            }
            if (rotRms > RotationGateDegrees)
            {
                reason = $"{side}: rotation rms {rotRms:0.0}° over gate {RotationGateDegrees:0.0}°";
                return false;
            }

            solved = new TrackerHandCalibration.SideParams
            {
                qx = rot.x, qy = rot.y, qz = rot.z, qw = rot.w,
                tx = translation.x, ty = translation.y, tz = translation.z,
                positionRms = posRms,
                rotationRmsDeg = rotRms,
                poseSet = "chest/side/front",
            };
            return true;
        }

        private static void ResetSlotsLocked()
        {
            _nextPose = 0;
            _left = new Sample[CalibrationPoses.Count];
            _right = new Sample[CalibrationPoses.Count];
        }
    }
}
