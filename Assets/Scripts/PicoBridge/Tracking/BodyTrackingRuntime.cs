using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Body-tracking start/stop lifecycle for the arm-source mode mutex (mocap map t07).
    ///
    /// PICO body tracking must be *started* before GetBodyTrackingData returns
    /// anything (the "sends but empty" diagnosis from the t06 subjective
    /// round: the panel blanket-enabled the Body flag but nobody ever called
    /// StartBodyTracking). Started with the full joint set (BodyTrackerRole
    /// nodes 0-23) and a bone-length table derived from one operator height,
    /// since the MANUS-gloves + controllers scenario cannot use the
    /// camera-based auto calibration.
    /// </summary>
    public static class BodyTrackingRuntime
    {
        // Segment lengths as a fraction of standing height (standard
        // anthropometric proportions; heuristic defaults — refine per
        // operator at HITL if the body skeleton looks off).
        private const float HeadFraction = 0.130f;
        private const float NeckFraction = 0.052f;
        private const float TorsoFraction = 0.235f;
        private const float HipFraction = 0.103f;
        private const float UpperLegFraction = 0.245f;
        private const float LowerLegFraction = 0.246f;
        private const float FootFraction = 0.152f;
        private const float ShoulderFraction = 0.245f;
        private const float UpperArmFraction = 0.186f;
        private const float LowerArmFraction = 0.146f;
        private const float HandFraction = 0.108f;

        private static bool _started;

        public static bool IsStarted => _started;

        /// <summary>Start full-body tracking with bone lengths for the given height (meters).</summary>
        public static void EnsureStarted(float operatorHeightM)
        {
            var height = Mathf.Clamp(operatorHeightM, 1.0f, 2.2f);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_started)
                return;

            var boneLength = BoneLengthForHeight(height);
            int result = PXR_MotionTracking.StartBodyTracking(
                BodyJointSet.BODY_JOINT_SET_BODY_FULL_START, boneLength);
            if (result == 0)
            {
                _started = true;
                Debug.Log($"[PicoBridge] Body tracking started (full set, height {height:0.00} m)");
            }
            else
            {
                Debug.LogWarning($"[PicoBridge] StartBodyTracking failed: {result} (height {height:0.00} m)");
            }
#else
            _started = true;
            Debug.Log($"[PicoBridge] Body tracking start skipped in Editor (height {height:0.00} m)");
#endif
        }

        public static void EnsureStopped()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_started)
                return;

            int result = PXR_MotionTracking.StopBodyTracking();
            _started = false;
            if (result == 0)
                Debug.Log("[PicoBridge] Body tracking stopped");
            else
                Debug.LogWarning($"[PicoBridge] StopBodyTracking failed: {result}");
#else
            _started = false;
#endif
        }

        public static BodyTrackingBoneLength BoneLengthForHeight(float heightM)
        {
            return new BodyTrackingBoneLength
            {
                headLen = HeadFraction * heightM,
                neckLen = NeckFraction * heightM,
                torsoLen = TorsoFraction * heightM,
                hipLen = HipFraction * heightM,
                upperLegLen = UpperLegFraction * heightM,
                lowerLegLen = LowerLegFraction * heightM,
                footLen = FootFraction * heightM,
                shoulderLen = ShoulderFraction * heightM,
                upperArmLen = UpperArmFraction * heightM,
                lowerArmLen = LowerArmFraction * heightM,
                handLen = HandFraction * heightM,
            };
        }
    }
}
