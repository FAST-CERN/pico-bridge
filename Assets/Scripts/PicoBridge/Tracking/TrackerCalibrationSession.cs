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
            public Vector3 HeadPos;     // raw head source at capture (t14
            public Quaternion HeadRot; // replay: target = head ∘ literal)
        }

        private static readonly object _lock = new object();
        private static State _state = State.Idle;
        private static int _nextPose;
        private static int _sessionId;      // t14: groups one Begin->solve round in the sample log
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
                _sessionId++;
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
                // Raw-sample observability (t14): logcat is dead on device
                // (chatty), so the round's ground truth goes to the JSONL
                // file at capture time — both sides, plus the head source
                // the target was composed from.
                TrackerCalibrationSampleLog.AppendSample(
                    _sessionId, _nextPose, "left",
                    leftSample.PuckPos, leftSample.PuckRot,
                    leftSample.TargetPos, leftSample.TargetRot,
                    leftSample.HeadPos, leftSample.HeadRot);
                TrackerCalibrationSampleLog.AppendSample(
                    _sessionId, _nextPose, "right",
                    rightSample.PuckPos, rightSample.PuckRot,
                    rightSample.TargetPos, rightSample.TargetRot,
                    rightSample.HeadPos, rightSample.HeadRot);
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
            sample.HeadPos = headPos;
            sample.HeadRot = headRot;
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
                TrackerCalibrationSampleLog.AppendCommitted(_sessionId, leftParams, rightParams);
                // Round observability: residuals are the gate-tuning signal
                // (t05 device round) — logcat is the only channel while the
                // store has no reader yet.
                Debug.Log($"[PicoBridge] Hand calibration committed: " +
                          $"L pos {leftParams.positionRms * 1000f:0.0}mm rot {leftParams.rotationRmsDeg:0.0}° " +
                          $"Rf {Quaternion.Angle(Quaternion.identity, new Quaternion(leftParams.fx, leftParams.fy, leftParams.fz, leftParams.fw)):0}° | " +
                          $"R pos {rightParams.positionRms * 1000f:0.0}mm rot {rightParams.rotationRmsDeg:0.0}° " +
                          $"Rf {Quaternion.Angle(Quaternion.identity, new Quaternion(rightParams.fx, rightParams.fy, rightParams.fz, rightParams.fw)):0}°");
                return;
            }

            _state = State.Rejected;
            _rejectReason = !leftOk ? leftReason : rightReason;
            TrackerCalibrationSampleLog.AppendRejected(_sessionId, _rejectReason);
            ResetSlotsLocked();
        }

        private static bool SolveSideLocked(
            Sample[] samples, string side,
            out TrackerHandCalibration.SideParams solved, out string reason)
        {
            solved = null;
            reason = "";

            // Hand-eye structure (2026-09-09 t07 eyeball round): the puck is
            // rigidly MOUNTED on the hand, so puck = hand compose M with a
            // constant LOCAL mount M — the runtime map is
            //   hand_rot = puck_rot * C      (C constant, right-multiplied)
            //   hand_pos = puck_pos + puck_rot * m   (offset rotates with the puck)
            // A GLOBAL R*p+t (what the first Kabsch formulation solved) is
            // structurally wrong for a mount: rotate the hand in place and
            // the puck position swings around, which no global rigid
            // transform can follow — the mapped gizmo floated away with no
            // fixed relation to the tracker (device verdict).
            //
            // Estimate: C = chordal mean of the per-sample relative
            // rotations puck_rot^-1 * target_rot; m = least squares on
            // (target - puck) - R(puck_rot) m.
            // AX=YB structure (2026-09-09 round 2): the head source and the
            // tracker cache frames differ by a large CONSTANT rotation R_f on
            // device (the local-only model rejected correct rounds at 0.6 m
            // pos rms; the earlier global fit absorbed R_f and measured
            // ~0.071 = the mount offset's rotation spread). Full model:
            //   target_rot = R_f * puck_rot * C
            //   target_pos = R_f * (puck_pos + puck_rot * m)
            //
            // Step 1: C from the RELATIVE rotation axes - conjugation gives
            // axis_puck_relative = C * axis_target_relative; Kabsch the axis
            // correspondences (3 pose pairs give 3 axes).
            var axesTarget = new System.Collections.Generic.List<Vector3>();
            var axesPuck = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < samples.Length - 1; i++)
                for (int j = i + 1; j < samples.Length; j++)
                {
                    var relTarget = Quaternion.Inverse(samples[i].TargetRot) * samples[j].TargetRot;
                    var relPuck = Quaternion.Inverse(samples[i].PuckRot) * samples[j].PuckRot;
                    // Canonical hemisphere (w >= 0): quaternion products land in
                    // either half, and the axis vector flips with the sign —
                    // the Kabsch correspondence needs a consistent convention.
                    if (relTarget.w < 0f)
                        relTarget = new Quaternion(-relTarget.x, -relTarget.y, -relTarget.z, -relTarget.w);
                    if (relPuck.w < 0f)
                        relPuck = new Quaternion(-relPuck.x, -relPuck.y, -relPuck.z, -relPuck.w);
                    axesTarget.Add(AxisOf(relTarget));
                    axesPuck.Add(AxisOf(relPuck));
                }
            Quaternion c;
            if (!KabschSolver.Solve(axesTarget.ToArray(), axesPuck.ToArray(), out var cRot, out _, out _))
            {
                reason = side + ": degenerate relative-rotation spread (poses too similar)";
                return false;
            }
            c = cRot;

            // Step 2: R_f as the chordal mean of target * (puck * C)^-1.
            var rfSum = new Quaternion(0f, 0f, 0f, 0f);
            Quaternion rfFirst = Quaternion.identity;
            for (int i = 0; i < samples.Length; i++)
            {
                var rf = samples[i].TargetRot * Quaternion.Inverse(samples[i].PuckRot * c);
                if (i == 0)
                    rfFirst = rf;
                if (Quaternion.Dot(rfFirst, rf) < 0f)
                    rf = new Quaternion(-rf.x, -rf.y, -rf.z, -rf.w);
                rfSum = new Quaternion(rfSum.x + rf.x, rfSum.y + rf.y, rfSum.z + rf.z, rfSum.w + rf.w);
            }
            var frameRot = Quaternion.Normalize(rfSum);

            // Step 3: m by 3x3 least squares on
            // d_i = target - R_f*puck = (R_f * puck_rot) * m.
            float[,] ata = new float[3, 3];
            float[] atd = new float[3];
            for (int i = 0; i < samples.Length; i++)
            {
                var rot = frameRot * samples[i].PuckRot;
                var cols = new[] { rot * Vector3.right, rot * Vector3.up, rot * Vector3.forward };
                var d = samples[i].TargetPos - frameRot * samples[i].PuckPos;
                for (int r = 0; r < 3; r++)
                {
                    atd[r] += Vector3.Dot(cols[r], d);
                    for (int cc = 0; cc < 3; cc++)
                        ata[r, cc] += Vector3.Dot(cols[r], cols[cc]);
                }
            }
            var m = Solve3x3(ata, atd);
            if (!m.HasValue)
            {
                reason = side + ": degenerate rotation spread (poses too similar)";
                return false;
            }

            // Residuals under the FULL model.
            float posSumSq = 0f, rotSumSq = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                var mapped = frameRot * (samples[i].PuckPos + samples[i].PuckRot * m.Value);
                posSumSq += (mapped - samples[i].TargetPos).sqrMagnitude;
                float angle = Quaternion.Angle(frameRot * samples[i].PuckRot * c, samples[i].TargetRot);
                rotSumSq += angle * angle;
            }
            float posRms = Mathf.Sqrt(posSumSq / samples.Length);
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
                qx = c.x, qy = c.y, qz = c.z, qw = c.w,
                fx = frameRot.x, fy = frameRot.y, fz = frameRot.z, fw = frameRot.w,
                tx = m.Value.x, ty = m.Value.y, tz = m.Value.z,
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

        /// <summary>Rotation axis of a quaternion (arbitrary for near-identity
        /// rotations; callers only feed well-spread relatives).</summary>
        private static Vector3 AxisOf(Quaternion q)
        {
            var axis = new Vector3(q.x, q.y, q.z);
            var norm = axis.magnitude;
            return norm < 1e-6f ? Vector3.up : axis / norm;
        }

        /// <summary>Solve a 3x3 system by Cramer; null when singular (the
        /// puck rotations did not spread across the poses).</summary>
        private static Vector3? Solve3x3(float[,] a, float[] b)
        {
            float det =
                a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) -
                a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) +
                a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
            if (Mathf.Abs(det) < 1e-6f)
                return null;

            float Det(float[,] m) =>
                m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

            float[] x = new float[3];
            for (int col = 0; col < 3; col++)
            {
                var modified = (float[,])a.Clone();
                for (int row = 0; row < 3; row++)
                    modified[row, col] = b[row];
                x[col] = Det(modified) / det;
            }
            return new Vector3(x[0], x[1], x[2]);
        }
    }
}
