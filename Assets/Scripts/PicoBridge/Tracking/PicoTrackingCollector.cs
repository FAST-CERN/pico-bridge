using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Unity.XR.PXR;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Collects tracking data from PICO native APIs and serializes it to bridge JSON.
    /// </summary>
    public class PicoTrackingCollector
    {
        private const int BodyJointCount = (int)BodyTrackerRole.ROLE_NUM;

        public bool HeadEnabled = true;
        public bool ControllerEnabled = true;
        public bool HandTrackingEnabled = true;
        public bool BodyTrackingEnabled = false;
        public bool MotionTrackerEnabled = false;
        public bool TrackerBodyEnabled = false;

        /// <summary>Operator height for the tracker-mode IK bone model; the
        /// manager syncs its serialized/remote-controlled value here each
        /// frame (same route as the stream flags).</summary>
        public float OperatorHeightM = 1.75f;

        private readonly StringBuilder _sb = new StringBuilder(4096);

        /// <summary>
        /// Collect all enabled tracking data and return as JSON string.
        /// </summary>
        public string CollectJson()
        {
            _sb.Clear();
            _sb.Append('{');

            double predictTimeMs = PXR_System.GetPredictedDisplayTime();
            long predictTimeUs = (long)(predictTimeMs * 1000.0);
            _sb.Append($"\"predictTime\":{predictTimeUs}");

            // App state
            _sb.Append(",\"appState\":{\"focus\":true}");

            // Head
            if (HeadEnabled)
                AppendHead(predictTimeMs);

            // Controllers
            if (ControllerEnabled)
                AppendControllers(predictTimeMs);

            // Hands
            if (HandTrackingEnabled)
                AppendHands();

            // Body: SDK body tracking, or the transitional tracker-mode frame
            // (t03). ApplyArmStream guarantees at most one is ever on
            // (TrackerBody also leaves sendBody true — tracker frames win).
            if (TrackerBodyEnabled)
                AppendTrackerBody();
            else if (BodyTrackingEnabled)
                AppendBody();

            // Motion trackers
            if (MotionTrackerEnabled)
                AppendMotion();

            // Input mask + timestamp
            _sb.Append(",\"Input\":0");
            long tsNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000);
            _sb.Append($",\"timeStampNs\":{tsNs}");

            _sb.Append('}');
            return _sb.ToString();
        }

        // ── Head ──────────────────────────────────────────

        private void AppendHead(double predictTimeMs)
        {
            PxrSensorState2 state = default;
            int frameIdx = 0;
            PXR_System.GetPredictedMainSensorStateNew(ref state, ref frameIdx);

            _sb.Append(",\"Head\":{\"pose\":\"");
            AppendPose(
                state.pose.position.x,
                state.pose.position.y,
                state.pose.position.z,
                state.pose.orientation.x,
                state.pose.orientation.y,
                state.pose.orientation.z,
                state.pose.orientation.w);
            _sb.Append($"\",\"status\":{state.status}}}");
        }

        // ── Controllers ───────────────────────────────────

        private void AppendControllers(double predictTimeMs)
        {
            _sb.Append(",\"Controller\":{");
            AppendController("left", PXR_Input.Controller.LeftController,
                InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller, predictTimeMs);
            _sb.Append(',');
            AppendController("right", PXR_Input.Controller.RightController,
                InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, predictTimeMs);
            _sb.Append('}');
        }

        private void AppendController(string side, PXR_Input.Controller ctrl, InputDeviceCharacteristics chars, double predictTimeMs)
        {
            Vector3 pos = PXR_Input.GetControllerPredictPosition(ctrl, predictTimeMs);
            Quaternion rot = PXR_Input.GetControllerPredictRotation(ctrl, predictTimeMs);

            _sb.Append($"\"{side}\":{{\"pose\":\"");
            AppendPose(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w);
            _sb.Append('"');

            // Read input via Unity InputDevices.
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(chars, devices);
            if (devices.Count > 0)
            {
                var dev = devices[0];
                dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis2D);
                dev.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var axisClick);
                dev.TryGetFeatureValue(CommonUsages.grip, out var grip);
                dev.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
                dev.TryGetFeatureValue(CommonUsages.primaryButton, out var primaryButton);
                dev.TryGetFeatureValue(CommonUsages.secondaryButton, out var secondaryButton);
                dev.TryGetFeatureValue(CommonUsages.menuButton, out var menuButton);

                _sb.Append(",\"axisX\":");
                AppendJsonNumber(axis2D.x);
                _sb.Append(",\"axisY\":");
                AppendJsonNumber(axis2D.y);
                _sb.Append($",\"axisClick\":{BoolStr(axisClick)}");
                _sb.Append(",\"grip\":");
                AppendJsonNumber(grip);
                _sb.Append(",\"trigger\":");
                AppendJsonNumber(trigger);
                _sb.Append($",\"primaryButton\":{BoolStr(primaryButton)}");
                _sb.Append($",\"secondaryButton\":{BoolStr(secondaryButton)}");
                _sb.Append($",\"menuButton\":{BoolStr(menuButton)}");
            }

            _sb.Append('}');
        }

        // ── Hands ─────────────────────────────────────────

        private void AppendHands()
        {
            _sb.Append(",\"Hand\":{");
            AppendHand("leftHand", HandType.HandLeft);
            _sb.Append(',');
            AppendHand("rightHand", HandType.HandRight);
            _sb.Append('}');
        }

        private void AppendHand(string key, HandType handType)
        {
            HandJointLocations joints = default;
            bool ok = PXR_HandTracking.GetJointLocations(handType, ref joints);

            _sb.Append($"\"{key}\":{{\"isActive\":{BoolStr(ok && joints.isActive > 0)}");
            _sb.Append($",\"count\":{(ok ? joints.jointCount : 0)}");
            _sb.Append(",\"scale\":");
            AppendJsonNumber(ok ? joints.handScale : 1f);

            if (ok && joints.isActive > 0 && joints.jointLocations != null)
            {
                _sb.Append(",\"HandJointLocations\":[");
                for (int i = 0; i < joints.jointLocations.Length; i++)
                {
                    if (i > 0) _sb.Append(',');
                    var j = joints.jointLocations[i];
                    _sb.Append("{\"p\":\"");
                    AppendPose(
                        j.pose.Position.x, j.pose.Position.y, j.pose.Position.z,
                        j.pose.Orientation.x, j.pose.Orientation.y, j.pose.Orientation.z, j.pose.Orientation.w);
                    _sb.Append($"\",\"s\":{(ulong)j.locationStatus},\"r\":");
                    AppendJsonNumber(j.radius);
                    _sb.Append('}');
                }
                _sb.Append(']');
            }
            else
            {
                _sb.Append(",\"HandJointLocations\":[]");
            }

            _sb.Append('}');
        }

        // ── Body ──────────────────────────────────────────

        private void AppendBody()
        {
            bool supported = false;
            PXR_MotionTracking.GetBodyTrackingSupported(ref supported);

            int count = 0;
            BodyTrackingData data = default;

            if (supported && TrackingSignalStatus.HasValidSignal(TrackingSignalKind.Body))
            {
                BodyTrackingGetDataInfo getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
                data = new BodyTrackingData();
                data.roleDatas = new BodyTrackingRoleData[BodyJointCount];
                int result = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);

                if (result == 0 && data.roleDatas != null)
                {
                    count = Mathf.Min(data.roleDatas.Length, BodyJointCount);
                }
            }

            _sb.Append(",\"Body\":{");
            _sb.Append("\"poseSpace\":\"pico_body_local\"");
            _sb.Append(",\"alignment\":\"pico_native\"");
            _sb.Append(",\"joints\":[");

            for (int i = 0; i < count; i++)
            {
                if (i > 0) _sb.Append(',');
                var rd = data.roleDatas[i];

                // Mount correction FIRST, in the PICO-native joint frame
                // (bodytrack-deploy t07/t09): the avatar renders native
                // poses (the SDK prefab only composes correctly with
                // native locals), and the wire output is the SAME corrected
                // pose after the standard native->Unity flip — one
                // correction, both views, tuned against what the operator
                // sees. Teleopit's correction layer stays null (no stacking).
                var pos = new Vector3(
                    (float)rd.localPose.PosX,
                    (float)rd.localPose.PosY,
                    (float)rd.localPose.PosZ);
                var rot = new Quaternion(
                    (float)rd.localPose.RotQx,
                    (float)rd.localPose.RotQy,
                    (float)rd.localPose.RotQz,
                    (float)rd.localPose.RotQw);
                BodyMountCorrection.Apply(i, ref pos, ref rot);

                // Avatar feed: corrected NATIVE pose (the block's own
                // rendering convention — original placement & config).
                BodyFrameCache.SetJoint(i, pos, rot, Time.realtimeSinceStartup);

                // Wire: the standard PICO-native -> Unity flip of the same
                // corrected pose (AppendBody contract).
                _sb.Append("{\"p\":\"");
                AppendPose(pos.x, pos.y, -pos.z, rot.x, rot.y, -rot.z, -rot.w);
                _sb.Append($"\",\"t\":{rd.localPose.TimeStamp}");
                unsafe
                {
                    AppendBodyVelocity(rd);
                }
                _sb.Append('}');
            }

            _sb.Append($"],\"len\":{count}}}");
        }

        private unsafe void AppendBodyVelocity(BodyTrackingRoleData rd)
        {
            _sb.Append(",\"va\":\"");
            AppendPoseComponent((float)rd.velo[0]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.velo[1]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.velo[2]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.acce[0]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.acce[1]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.acce[2]);
            _sb.Append("\",\"wva\":\"");
            AppendPoseComponent((float)rd.wvelo[0]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.wvelo[1]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.wvelo[2]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.wacce[0]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.wacce[1]);
            _sb.Append(',');
            AppendPoseComponent((float)rd.wacce[2]);
            _sb.Append('"');
        }

        // ── Tracker-mode body frame (t03 channel, t09 content) ──

        // Same Body wire contract as AppendBody (poseSpace/alignment/24
        // joints/len), but the content is the t08 IK: head/neck chain from
        // the HMD source, arm chains from the trimmed tracker hands, lower
        // body = static standing template whose root follows the head.
        //
        // Frame ruling (t09, from the 2026-09-09 recording finding): body
        // joints are COMMON-frame poses, the wire carries the flipped
        // Unity-convention values, and the IK solves in exactly that
        // convention — so solved joints serialize VERBATIM (unlike
        // AppendBody, which flips its native SDK inputs; the head source is
        // native and gets flipped ONCE here on the way in). The avatar cache
        // keeps its native semantics: every solved pose is flipped
        // Unity→native before BodyFrameCache.SetJoint, so the SDK avatar
        // renders in tracker mode exactly like body mode.
        // BodyMountCorrection does not intervene (t03 ruling: the t17 trim
        // already carries the mount geometry — no double correction).
        private UpperBodyIkSolver _ikSolver;
        private float _ikSolverHeight;

        private void AppendTrackerBody()
        {
            var headPos = Vector3.zero;
            var headRot = Quaternion.identity;
            long t = 0;
            var source = TrackerBodyHead.Source;
            if (source != null && source(out headPos, out headRot, out t))
            {
                // native → Unity: the same flip AppendBody applies at
                // serialize — the IK solves in the frame the (already
                // flipped) tracker hands live in.
                headPos = new Vector3(headPos.x, headPos.y, -headPos.z);
                headRot = new Quaternion(headRot.x, headRot.y, -headRot.z, -headRot.w);
            }
            else
            {
                headPos = Vector3.zero;
                headRot = Quaternion.identity;
                t = 0;
            }

            if (_ikSolver == null || !Mathf.Approximately(_ikSolverHeight, OperatorHeightM))
            {
                _ikSolver = new UpperBodyIkSolver(OperatorHeightM);
                _ikSolverHeight = OperatorHeightM;
            }

            var result = _ikSolver.Solve(new UpperBodyIkSolver.FrameInput
            {
                HeadPosition = headPos,
                HeadRotation = headRot,
                Left = ReadTrackerHand("left"),
                Right = ReadTrackerHand("right"),
                NowSeconds = Time.realtimeSinceStartupAsDouble,
            });

            _sb.Append(",\"Body\":{");
            _sb.Append("\"poseSpace\":\"pico_body_local\"");
            _sb.Append(",\"alignment\":\"pico_native\"");
            _sb.Append(",\"joints\":[");
            var now = Time.realtimeSinceStartup;
            for (int i = 0; i < BodyJointCount; i++)
            {
                if (i > 0) _sb.Append(',');
                var pos = result.Positions[i];
                var rot = result.Rotations[i];
                _sb.Append("{\"p\":\"");
                AppendPose(pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w);
                _sb.Append($"\",\"t\":{t}");
                _sb.Append(",\"va\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"");
                _sb.Append(",\"wva\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"");
                _sb.Append('}');

                // Avatar feed: the cache's native convention = flip of the
                // solved (Unity/wire) pose.
                BodyFrameCache.SetJoint(
                    i,
                    new Vector3(pos.x, pos.y, -pos.z),
                    new Quaternion(rot.x, rot.y, -rot.z, -rot.w),
                    now);
            }
            _sb.Append($"],\"len\":{BodyJointCount}}}");
        }

        /// <summary>One side's IK hand input from the tracker cache: fresh
        /// optically-valid side → TryMap (t17 trim) pose; anything else
        /// (stale, absent, invalid) → invalid, which the solver's state
        /// machine turns into Held-then-Static.</summary>
        private static UpperBodyIkSolver.HandInput ReadTrackerHand(string side)
        {
            if (!TrackerFrameCache.TryGetFrame(side, out var frame) ||
                !TrackerFrameCache.IsFresh(frame, TrackerFrameCache.Clock()) ||
                !frame.HasPose || !frame.Valid)
                return default;
            if (!TrackerHandCalibration.TryMap(side, frame.Position, frame.Rotation, out var pos, out var rot))
                return default;
            return new UpperBodyIkSolver.HandInput { Valid = true, Position = pos, Rotation = rot };
        }

        // ── Motion Trackers ───────────────────────────────

        // Wire contract (receiver 0.2.2, mocap map t04): side-first
        // Motion.left/right, each {"sn":<long>,"p":"x,y,z,qx,qy,qz,qw",
        // "valid":<bool>}. An unbound or disconnected side is omitted
        // entirely so the receiver reports it inactive. Poses ride the same
        // PICO-native-local -> Unity flip as AppendBody (-Z, -Qz, -Qw) under
        // poseSpace "pico_tracker_local" (t01 §4); downstream transform
        // (_INPUT_TO_TELEOPIT_MATRIX chain) is unchanged.
        private void AppendMotion()
        {
            MotionTrackerBinding.EnsureStarted();

            _sb.Append(",\"Motion\":{\"poseSpace\":\"pico_tracker_local\"");
            AppendTrackerSide("left");
            AppendTrackerSide("right");
            _sb.Append('}');
        }

        private void AppendTrackerSide(string side)
        {
            // t01: the poller is the single acquisition authority — read the
            // flipped cache instead of polling PXR here. A stale (or absent)
            // side is omitted so the receiver reports it inactive.
            if (!TrackerFrameCache.TryGetFrame(side, out var frame) ||
                !TrackerFrameCache.IsFresh(frame, TrackerFrameCache.Clock()))
                return;

            // An invalid side that never had a pose serializes a zero pose so
            // the receiver still sees valid:false (the old inline path's
            // semantics on optical loss).
            var p = frame.HasPose ? frame.Position : Vector3.zero;
            var q = frame.HasPose ? frame.Rotation : Quaternion.identity;
            _sb.Append($",\"{side}\":{{\"sn\":{frame.Sn},\"p\":\"");
            AppendPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
            _sb.Append($"\",\"valid\":{BoolStr(frame.Valid)}}}");
        }

        // ── helpers ───────────────────────────────────────

        private void AppendPose(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            AppendPoseComponent(x);
            _sb.Append(',');
            AppendPoseComponent(y);
            _sb.Append(',');
            AppendPoseComponent(z);
            _sb.Append(',');
            AppendPoseComponent(qx);
            _sb.Append(',');
            AppendPoseComponent(qy);
            _sb.Append(',');
            AppendPoseComponent(qz);
            _sb.Append(',');
            AppendPoseComponent(qw);
        }

        private void AppendPoseComponent(float value)
        {
            _sb.Append(JsonNumber(value, "F6"));
        }

        private void AppendJsonNumber(float value)
        {
            _sb.Append(JsonNumber(value, "F4"));
        }

        private static string JsonNumber(float value, string format)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "0";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string BoolStr(bool v) => v ? "true" : "false";
    }
}
