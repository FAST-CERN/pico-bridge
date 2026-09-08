using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Timed in-app guide around TrackerCalibrationSession (tracker-ik map
    /// t06). The 2026-09-08 device round rejected PC-audio beats as the pose
    /// cue (operator mistimed every pose -> 0.325 m residual): guidance and
    /// countdowns must live on the panel, in-headset.
    ///
    /// Per pose: Prep (guidance text + PrepSeconds countdown) -> Hold (same
    /// text + HoldSeconds countdown, capture on the beat). A capture that
    /// misses (stale/invalid frames fill no slot) retries after
    /// RecaptureSeconds; MaxRecaptureAttempts consecutive misses give up
    /// with a tracking-lost verdict. The third capture auto-solves inside
    /// the session; this guide renders the verdict for VerdictSeconds and
    /// returns to Inactive.
    ///
    /// Tick(dt) is driven from PicoBridgeManager.Update on device; the
    /// editor smoke drives it directly. StatusText is pure ASCII (panel
    /// font has no CJK glyphs, t01 device lesson 8c79e9c).
    /// </summary>
    public static class TrackerCalibrationGuide
    {
        public enum Phase { Inactive, Prep, Hold, Recapture, Verdict }

        public const float PrepSeconds = 5f;
        public const float HoldSeconds = 15f;
        public const float RecaptureSeconds = 3f;
        public const float VerdictSeconds = 6f;
        public const int MaxRecaptureAttempts = 5;

        private static Phase _phase = Phase.Inactive;
        private static int _poseIndex;
        private static float _remaining;
        private static int _recaptureAttempts;
        private static bool _trackingLostVerdict;
        private static float _immersiveCheckTimer;

        public static Phase CurrentPhase => _phase;
        public static int PoseIndex => _poseIndex;
        public static float RemainingSeconds => _remaining;
        public static bool IsUrgent => _phase == Phase.Hold && _remaining <= 3f;

        /// <summary>Test seam: pristine Inactive.</summary>
        public static void ResetForTest()
        {
            _phase = Phase.Inactive;
            _poseIndex = 0;
            _remaining = 0f;
            _recaptureAttempts = 0;
            _trackingLostVerdict = false;
        }

        public static void Start()
        {
            TrackerCalibrationSession.Begin();
            _poseIndex = 0;
            _recaptureAttempts = 0;
            _trackingLostVerdict = false;
            EnterPrep();
        }

        public static void Abort()
        {
            TrackerCalibrationSession.Abort();
            ResetForTest();
        }

        public static void Tick(float deltaSeconds)
        {
            if (_phase == Phase.Inactive)
                return;

#if !UNITY_EDITOR
            // The panel (and its guidance text) hides in stereo immersive
            // mode; a blind capture is worse than none, so the flow aborts.
            _immersiveCheckTimer += deltaSeconds;
            if (_immersiveCheckTimer >= 1f)
            {
                _immersiveCheckTimer = 0f;
                var immersive = Object.FindObjectOfType<PicoBridge.Immersive.StereoImmersiveController>();
                if (immersive != null && immersive.IsImmersiveActive)
                {
                    Abort();
                    return;
                }
            }
#endif

            _remaining -= deltaSeconds;
            if (_remaining > 0f)
                return;

            switch (_phase)
            {
                case Phase.Prep:
                    _remaining = HoldSeconds;
                    _phase = Phase.Hold;
                    break;
                case Phase.Hold:
                case Phase.Recapture:
                    PushCapture();
                    break;
                case Phase.Verdict:
                    ResetForTest();
                    break;
            }
        }

        private static void EnterPrep()
        {
            _remaining = PrepSeconds;
            _phase = Phase.Prep;
        }

        private static void PushCapture()
        {
            bool captured = TrackerCalibrationSession.Capture();
            _recaptureAttempts = captured ? 0 : _recaptureAttempts + 1;

            if (!captured)
            {
                if (_recaptureAttempts >= MaxRecaptureAttempts)
                {
                    _trackingLostVerdict = true;
                    _remaining = VerdictSeconds;
                    _phase = Phase.Verdict;
                    return;
                }
                _remaining = RecaptureSeconds;
                _phase = Phase.Recapture;
                return;
            }

            var state = TrackerCalibrationSession.CurrentState;
            if (state == TrackerCalibrationSession.State.Committed ||
                state == TrackerCalibrationSession.State.Rejected)
            {
                _trackingLostVerdict = false;
                _remaining = VerdictSeconds;
                _phase = Phase.Verdict;
                return;
            }

            _poseIndex = TrackerCalibrationSession.NextPoseIndex;
            EnterPrep();
        }

        /// <summary>Panel copy for the current phase (ASCII only).</summary>
        public static string StatusText()
        {
            switch (_phase)
            {
                case Phase.Inactive:
                    return "";
                case Phase.Prep:
                    return $"POSE {_poseIndex + 1}/3 {Caption()} : GET READY {Mathf.CeilToInt(_remaining)}";
                case Phase.Hold:
                    return $"POSE {_poseIndex + 1}/3 {Caption()} : HOLD {Mathf.CeilToInt(_remaining)}";
                case Phase.Recapture:
                    return $"TRACKING LOST - HOLD STILL : {Mathf.CeilToInt(_remaining)}";
                case Phase.Verdict:
                    if (_trackingLostVerdict)
                        return "TRACKING LOST - check pucks, press CALIB";
                    var state = TrackerCalibrationSession.CurrentState;
                    if (state == TrackerCalibrationSession.State.Rejected)
                        return $"CALIB REJECTED - {TrackerCalibrationSession.LastRejectReason}";
                    return $"CALIB OK - L {DescribeSide("left")}  R {DescribeSide("right")}";
                default:
                    return "";
            }
        }

        private static string Caption()
        {
            string name = CalibrationPoses.Name(_poseIndex);
            return $"{name.ToUpper()} - {CalibrationPoses.Instruction(_poseIndex)}";
        }

        private static string DescribeSide(string side)
        {
            var entry = TrackerHandCalibration.GetSide(side);
            if (entry == null)
                return "n/a";
            return $"{entry.positionRms * 1000f:0.0}mm/{entry.rotationRmsDeg:0.0}d";
        }
    }
}
