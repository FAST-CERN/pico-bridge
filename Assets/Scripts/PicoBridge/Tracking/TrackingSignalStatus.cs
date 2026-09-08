using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;
#if PICO_OPENXR_SDK
using Unity.XR.OpenXR.Features.PICOSupport;
#endif

namespace PicoBridge.Tracking
{
    public enum TrackingSignalKind
    {
        Head,
        LeftController,
        RightController,
        LeftHand,
        RightHand,
        Body,
        Motion
    }

    public static class TrackingSignalStatus
    {
        // t10 (2026-09-09): tracker-mode body frames are a valid Body
        // signal for the visual gates. The SDK body tracking is stopped by
        // the arm-source mode mutex in TrackerBody mode, which used to read
        // as "no body signal" — the TrackingVisualSignalGate on the avatar
        // disabled every renderer and the operator saw nothing while the
        // t09 IK streamed perfect frames. AppendTrackerBody notes each
        // frame; a frame within TrackerBodySignalFreshS counts as valid.
        private const float TrackerBodySignalFreshS = 1.0f;
        private static float _trackerBodyFrameAt = float.NegativeInfinity;

        /// <summary>Called by AppendTrackerBody on every tracker-mode body
        /// frame (main thread).</summary>
        public static void NoteTrackerBodyFrame()
        {
            _trackerBodyFrameAt = Time.unscaledTime;
        }

        /// <summary>True when tracker-mode body frames flowed recently —
        /// the tracker-mode equivalent of a valid SDK body signal.</summary>
        public static bool HasRecentTrackerBodyFrames()
        {
            return Time.unscaledTime - _trackerBodyFrameAt <= TrackerBodySignalFreshS;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static readonly List<InputDevice> Devices = new List<InputDevice>();

        private static readonly HashSet<long> MotionTrackerIds = new HashSet<long>();
        private static bool _subscribedToMotionTrackerEvents;
#endif

        public static bool HasValidSignal(TrackingSignalKind signal)
        {
#if UNITY_EDITOR
            return true;
#elif UNITY_ANDROID
            switch (signal)
            {
                case TrackingSignalKind.Head:
                    return HasValidHeadSignal();
                case TrackingSignalKind.LeftController:
                    return HasValidControllerSignal(InputDeviceCharacteristics.Left);
                case TrackingSignalKind.RightController:
                    return HasValidControllerSignal(InputDeviceCharacteristics.Right);
                case TrackingSignalKind.LeftHand:
#if !PICO_OPENXR_SDK
                    return HasValidHandSignal(HandType.HandLeft);
#else
                    return false;
#endif
                case TrackingSignalKind.RightHand:
#if !PICO_OPENXR_SDK
                    return HasValidHandSignal(HandType.HandRight);
#else
                    return false;
#endif
                case TrackingSignalKind.Body:
                    return HasValidBodySignal() || HasRecentTrackerBodyFrames();
                case TrackingSignalKind.Motion:
                    return HasValidMotionTrackerSignal();
                default:
                    return false;
            }
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool HasValidHeadSignal()
        {
            if (HasValidInputDeviceSignal(InputDeviceCharacteristics.HeadMounted))
                return true;

            PxrSensorState2 state = default;
            int frameIndex = 0;
            PXR_System.GetPredictedMainSensorStateNew(ref state, ref frameIndex);
            return state.status != 0;
        }

        private static bool HasValidControllerSignal(InputDeviceCharacteristics hand)
        {
            return HasValidInputDeviceSignal(hand | InputDeviceCharacteristics.Controller);
        }

        private static bool HasValidInputDeviceSignal(InputDeviceCharacteristics characteristics)
        {
            Devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(characteristics, Devices);

            foreach (var device in Devices)
            {
                if (device.TryGetFeatureValue(CommonUsages.isTracked, out var isTracked) && isTracked)
                    return true;

                if (device.TryGetFeatureValue(CommonUsages.trackingState, out var trackingState) &&
                    (trackingState & (InputTrackingState.Position | InputTrackingState.Rotation)) != 0)
                    return true;
            }

            return false;
        }

        private static bool HasValidBodySignal()
        {
            var tracking = false;
            var status = new BodyTrackingStatus();
#if PICO_OPENXR_SDK
            BodyTrackingFeature.GetBodyTrackingState(ref tracking, ref status);
#else
            PXR_MotionTracking.GetBodyTrackingState(ref tracking, ref status);
#endif
            return tracking && status.stateCode == BodyTrackingStatusCode.BT_VALID;
        }

        private static bool HasValidMotionTrackerSignal()
        {
            EnsureMotionTrackerEventSubscription();

#if PICO_OPENXR_SDK
            return false;
#else
            foreach (var trackerId in MotionTrackerIds)
            {
                var location = new MotionTrackerLocation();
                var isValidPose = false;
                if (PXR_MotionTracking.GetMotionTrackerLocation(trackerId, ref location, ref isValidPose) == 0 && isValidPose)
                    return true;
            }

            return false;
#endif
        }

        private static void EnsureMotionTrackerEventSubscription()
        {
            if (_subscribedToMotionTrackerEvents)
                return;

            PXR_MotionTracking.MotionTrackerConnectionAction += OnMotionTrackerConnectionChanged;
            _subscribedToMotionTrackerEvents = true;
        }

        private static void OnMotionTrackerConnectionChanged(long trackerId, int state)
        {
            if (state == 1)
                MotionTrackerIds.Add(trackerId);
            else
                MotionTrackerIds.Remove(trackerId);
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR && !PICO_OPENXR_SDK
        private static bool HasValidHandSignal(HandType handType)
        {
            var joints = new HandJointLocations();
            return PXR_HandTracking.GetJointLocations(handType, ref joints) &&
                   joints.isActive > 0 &&
                   joints.jointLocations != null &&
                   joints.jointCount > 0;
        }
#endif
    }
}
