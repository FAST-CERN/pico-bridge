using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Upper-body IK for tracker-mode body frames (tracker-ik map t08).
    ///
    /// Input: HMD pose + per-side hand poses (TrackerHandCalibration.TryMap
    /// output — position at the wrist root, orientation in the palm
    /// convention: +Z back of hand, +X fingers, +Y right). Output: 24 joint
    /// poses in the common pico_body_local frame — the 2026-09-09 recording
    /// finding: the SDK's "localPose" values are absolute coordinates in one
    /// body-local frame (Pelvis y ~1.0, Head y ~1.6), not parent-relative,
    /// and every consumer (avatar hierarchy, Teleopit retarget SE3 targets,
    /// stick viewer) treats them that way. All math here runs in the
    /// Unity-side frame; t09 flips at serialization like AppendBody.
    ///
    /// Design (grilled 2026-09-09 04:28, seven rulings — see ticket 08):
    /// - Root follows the head: the standing template (literal from the
    ///   20260904_231338 recording, height-scaled) translates with the
    ///   head's horizontal position and yaws with the head yaw.
    /// - Closed-form two-link arm solve: the elbow-flexion axial component
    ///   α comes from the shoulder-wrist distance alone
    ///   (law of cosines), the swivel from the hand orientation's radial
    ///   component p̂ (fingers axis projected ⊥ the shoulder-wrist axis):
    ///   E = W − L2·(α·axis + β·p̂), β = √(1−α²) — both bone lengths stay
    ///   rigidly exact by construction; axial mismatch is absorbed by the
    ///   wrist joint (wrist orientation outputs R_hand ∘ C′ — the t20
    ///   MEASURED palm→SDK-wrist constant, see WristPalmConvention).
    /// - Unreachable targets clamp ONTO the reach sphere (straight-arm
    ///   semantics, rigid bones); no joint-limit tables in v1.
    /// - A lost side freezes its last solved arm for HoldSeconds, then
    ///   relaxes to the root-placed template arm (never-seen = template
    ///   arm immediately).
    /// - Bone lengths: BodyTrackingRuntime fractions × operatorHeight;
    ///   shoulder anchors: synth-measured head-frame constants; wrist
    ///   convention constants: ported from the Teleopit synth table —
    ///   residual per-side offsets are absorbed by the human-trim knobs
    ///   (t17), no new calibration protocol.
    /// </summary>
    public sealed class UpperBodyIkSolver
    {
        public const int JointCount = 24;

        // Receiver BODY_JOINT_NAMES order (== SDK BodyTrackerRole order).
        public const int Pelvis = 0;
        public const int Neck = 12;
        public const int LeftCollar = 13;
        public const int RightCollar = 14;
        public const int Head = 15;
        public const int LeftShoulder = 16;
        public const int RightShoulder = 17;
        public const int LeftElbow = 18;
        public const int RightElbow = 19;
        public const int LeftWrist = 20;
        public const int RightWrist = 21;
        public const int LeftHand = 22;
        public const int RightHand = 23;

        // ── anthropometry (t08 grill ruling 5: three-source combo) ──

        /// <summary>Standing height of the recording the template came from.</summary>
        public const float ReferenceHeightM = 1.70f;

        // Segment-length fractions (BodyTrackingRuntime table).
        public const float UpperArmFraction = 0.186f;
        public const float ForearmFraction = 0.146f;

        /// <summary>Wrist→hand-joint segment (synth precedent; recording
        /// measured 0.097).</summary>
        public const float HandSegmentM = 0.09f;

        // Head-frame anchor constants (ported from the Teleopit synth
        // SynthConfig: measured-against-operator head rig).
        public const float NeckDropM = 0.06f;
        public const float NeckShoulderDropM = 0.28f;
        public const float ShoulderWidthM = 0.38f;
        public static readonly Vector3 ChestOffsetM = new Vector3(0f, 0f, 0.03f);

        // ── robustness (t08 grill rulings 3/4) ──

        /// <summary>A lost side holds its last solved arm this long before
        /// relaxing to the template arm.</summary>
        public const float HoldSeconds = 1.0f;

        /// <summary>Reach-sphere shrink so the straight-arm boundary stays
        /// numerically inside |α| ≤ 1.</summary>
        public const float ReachEpsilonM = 5e-4f;

        /// <summary>Flexion-plane normal stabilization fraction (synth
        /// _PLANE_NORMAL_BLEND — continuous handover as the arm
        /// straightens).</summary>
        public const float PlaneNormalBlend = 0.05f;

        // ── orientation convention constants (ported Teleopit synth
        // _ARM_CONVENTION_QUATS, estimated 2026-09-05 from the real body
        // recording; Unity xyzw, per joint per side). Normalized on
        // construction: the table is rounded to 3 decimals, and Unity
        // quaternions carry whatever norm they're given (scipy normalized
        // silently on the Python side) — non-unit constants skew every
        // downstream Quaternion.Angle and leak onto the wire. ──

        public static readonly Quaternion LeftShoulderConvention = Quaternion.Normalize(
            new Quaternion(-0.047f, 0.052f, -0.997f, -0.025f));
        public static readonly Quaternion LeftElbowConvention = Quaternion.Normalize(
            new Quaternion(0.030f, 0.126f, -0.991f, -0.015f));
        public static readonly Quaternion RightShoulderConvention = Quaternion.Normalize(
            new Quaternion(-0.030f, 0.062f, 0.023f, 0.997f));
        public static readonly Quaternion RightElbowConvention = Quaternion.Normalize(
            new Quaternion(-0.110f, -0.130f, 0.038f, 0.985f));

        // ── t20: palm convention → SDK wrist joint quat (MEASURED, not
        // derived — ticket 20's measurement record) ──

        /// <summary>C′ := P⁻¹·Q — the palm-convention frame onto the SDK
        /// wrist joint quat, a true constant (both frames are rigid on the
        /// hand). Measured on device 2026-09-09 08:45 from a body-mode
        /// reference-pose hold (4741 frames, per-frame spread 2.3-2.5°;
        /// Teleopit scripts/dev/analyze_t20_body_ref.py on
        /// data/pico_tracker_calib/2026-09-09/t20_bodyref2.jsonl).
        /// Replaces the ported C_wrist composition, which was ~85° off per
        /// side: the synth constant carries the source recording's
        /// hanging-wrist state and only holds against the anatomical
        /// forearm frame it was fitted with — never against the measured
        /// palm frame. Per-side constants; chirality lives here.
        /// Normalized on construction (3-decimal table rounding).</summary>
        public static readonly Quaternion LeftWristPalmConvention = Quaternion.Normalize(
            new Quaternion(-0.156f, 0.621f, 0.767f, 0.050f));
        public static readonly Quaternion RightWristPalmConvention = Quaternion.Normalize(
            new Quaternion(0.620f, -0.156f, 0.005f, 0.769f));

        // ── static template: first standing frame of the 20260904_231338
        // recording (arms hanging naturally). Common-frame values in the
        // post-flip Unity convention, at ReferenceHeightM. ──

        private struct JointTemplate
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public JointTemplate(Vector3 position, Quaternion rotation)
            {
                Position = position;
                Rotation = rotation.normalized; // recording literals are 6-decimal rounded; keep unit norm at the source
            }
        }

        private static readonly JointTemplate[] RecordingTemplate =
        {
            // Pelvis
            new JointTemplate(new Vector3(+0.0370f, +0.9946f, +0.0997f), new Quaternion(-0.022196f, +0.219311f, -0.010064f, +0.975351f)),
            // Left_Hip
            new JointTemplate(new Vector3(-0.0135f, +0.9037f, +0.1531f), new Quaternion(+0.015416f, +0.203684f, -0.022260f, +0.978662f)),
            // Right_Hip
            new JointTemplate(new Vector3(+0.1024f, +0.8964f, +0.0925f), new Quaternion(+0.034706f, +0.193419f, -0.021042f, +0.980276f)),
            // Spine1
            new JointTemplate(new Vector3(+0.0520f, +1.1142f, +0.1328f), new Quaternion(-0.094664f, +0.241577f, +0.017990f, +0.965586f)),
            // Left_Knee
            new JointTemplate(new Vector3(-0.0791f, +0.5158f, +0.1499f), new Quaternion(+0.013312f, +0.138960f, +0.010162f, +0.990156f)),
            // Right_Knee
            new JointTemplate(new Vector3(+0.1218f, +0.5018f, +0.0514f), new Quaternion(-0.016452f, +0.196202f, -0.041924f, +0.979529f)),
            // Spine2
            new JointTemplate(new Vector3(+0.0209f, +1.2544f, +0.0818f), new Quaternion(-0.092385f, +0.231098f, +0.011637f, +0.968464f)),
            // Left_Ankle
            new JointTemplate(new Vector3(-0.0463f, +0.1072f, +0.1709f), new Quaternion(+0.007984f, +0.197304f, +0.024639f, +0.980000f)),
            // Right_Ankle
            new JointTemplate(new Vector3(+0.0866f, +0.0969f, +0.1119f), new Quaternion(-0.037830f, +0.141883f, -0.082212f, +0.985738f)),
            // Spine3
            new JointTemplate(new Vector3(+0.0182f, +1.3151f, +0.0677f), new Quaternion(-0.101145f, +0.228038f, +0.021769f, +0.968140f)),
            // Left_Foot
            new JointTemplate(new Vector3(-0.1262f, +0.0465f, +0.0755f), new Quaternion(+0.007987f, +0.197320f, +0.024624f, +0.979997f)),
            // Right_Foot
            new JointTemplate(new Vector3(+0.0741f, +0.0285f, -0.0087f), new Quaternion(-0.037911f, +0.141863f, -0.082249f, +0.985735f)),
            // Neck
            new JointTemplate(new Vector3(+0.0276f, +1.5464f, +0.0513f), new Quaternion(-0.147838f, +0.245223f, +0.005378f, +0.958113f)),
            // Left_Collar
            new JointTemplate(new Vector3(-0.0553f, +1.4446f, +0.0984f), new Quaternion(-0.095335f, +0.171510f, +0.043493f, +0.979594f)),
            // Right_Collar
            new JointTemplate(new Vector3(+0.1000f, +1.4409f, +0.0272f), new Quaternion(-0.140211f, +0.313984f, +0.003549f, +0.939011f)),
            // Head
            new JointTemplate(new Vector3(-0.0106f, +1.6048f, -0.0057f), new Quaternion(-0.268575f, +0.205010f, +0.053224f, +0.939684f)),
            // Left_Shoulder
            new JointTemplate(new Vector3(-0.1478f, +1.4278f, +0.1728f), new Quaternion(-0.007400f, +0.171348f, +0.646880f, +0.743055f)),
            // Right_Shoulder
            new JointTemplate(new Vector3(+0.2065f, +1.4213f, -0.0164f), new Quaternion(-0.320679f, +0.212954f, -0.586002f, +0.713034f)),
            // Left_Elbow
            new JointTemplate(new Vector3(-0.1554f, +1.1791f, +0.2620f), new Quaternion(+0.068271f, +0.040477f, +0.660448f, +0.746666f)),
            // Right_Elbow
            new JointTemplate(new Vector3(+0.2765f, +1.1752f, +0.0341f), new Quaternion(-0.200266f, +0.297919f, -0.594201f, +0.719766f)),
            // Left_Wrist
            new JointTemplate(new Vector3(-0.1874f, +0.9589f, +0.2330f), new Quaternion(+0.144963f, -0.000045f, +0.684725f, +0.714240f)),
            // Right_Wrist
            new JointTemplate(new Vector3(+0.2794f, +0.9434f, -0.0048f), new Quaternion(-0.184456f, +0.257189f, -0.659839f, +0.681500f)),
            // Left_Hand
            new JointTemplate(new Vector3(-0.1785f, +0.8622f, +0.2281f), new Quaternion(+0.144963f, -0.000045f, +0.684725f, +0.714240f)),
            // Right_Hand
            new JointTemplate(new Vector3(+0.2781f, +0.8452f, -0.0007f), new Quaternion(-0.184456f, +0.257189f, -0.659839f, +0.681500f)),
        };

        // Arm-chain joints per side (template fallback + freeze copy).
        private static readonly int[] LeftArmJoints = { LeftCollar, LeftShoulder, LeftElbow, LeftWrist, LeftHand };
        private static readonly int[] RightArmJoints = { RightCollar, RightShoulder, RightElbow, RightWrist, RightHand };

        /// <summary>One side's hand input in a frame.</summary>
        public struct HandInput
        {
            public bool Valid;         // side TRACKED (t19: fresh cache, pose seen) — optical Valid is a gizmo-only concern
            public Vector3 Position;   // wrist root (TryMap output)
            public Quaternion Rotation; // palm convention: +Z back, +X fingers, +Y right
        }

        /// <summary>One frame of IK input; all poses in the Unity-side
        /// common frame.</summary>
        public struct FrameInput
        {
            public Vector3 HeadPosition;
            public Quaternion HeadRotation;
            public HandInput Left;
            public HandInput Right;
            public double NowSeconds;
        }

        public enum SideState { Static, Held, Live }

        /// <summary>Solve output. The arrays are REUSED between Solve calls
        /// — copy them if you need to keep a frame (the t09 serializer
        /// consumes immediately).</summary>
        public struct Result
        {
            public Vector3[] Positions;
            public Quaternion[] Rotations;
            public SideState LeftState;
            public SideState RightState;
        }

        public readonly float UpperArmLength;
        public readonly float ForearmLength;
        public readonly float HeightScale;

        private readonly Vector3[] _positions = new Vector3[JointCount];
        private readonly Quaternion[] _rotations = new Quaternion[JointCount];
        private readonly Vector3[] _frozenLeft = new Vector3[LeftArmJoints.Length];
        private readonly Quaternion[] _frozenLeftRot = new Quaternion[LeftArmJoints.Length];
        private readonly Vector3[] _frozenRight = new Vector3[RightArmJoints.Length];
        private readonly Quaternion[] _frozenRightRot = new Quaternion[RightArmJoints.Length];
        private double _lastValidLeft = double.NegativeInfinity;
        private double _lastValidRight = double.NegativeInfinity;
        private bool _hasFrozenLeft;
        private bool _hasFrozenRight;

        public UpperBodyIkSolver(float operatorHeightM)
        {
            var height = Mathf.Clamp(operatorHeightM, 1.0f, 2.2f);
            UpperArmLength = UpperArmFraction * height;
            ForearmLength = ForearmFraction * height;
            HeightScale = height / ReferenceHeightM;
        }

        /// <summary>Shoulder anchor in the head frame (synth constants).</summary>
        public static Vector3 ShoulderAnchor(Vector3 headPos, Quaternion headRot, bool left)
        {
            float side = left ? -1f : 1f;
            return headPos + headRot * (ChestOffsetM + new Vector3(side * 0.5f * ShoulderWidthM, -NeckShoulderDropM, 0f));
        }

        /// <summary>Solve one frame. Pure compute; no allocations after
        /// construction.</summary>
        public Result Solve(FrameInput frame)
        {
            // ── torso/root: standing template yawed with the head and
            // translated to the head's horizontal position ──
            var forward = frame.HeadRotation * Vector3.forward;
            var flat = new Vector3(forward.x, 0f, forward.z);
            var yawRot = flat.sqrMagnitude > 1e-10f
                ? Quaternion.LookRotation(flat.normalized, Vector3.up)
                : Quaternion.identity;
            var pelvisTemplate = RecordingTemplate[Pelvis].Position;
            var pelvisTarget = new Vector3(
                frame.HeadPosition.x,
                pelvisTemplate.y * HeightScale,
                frame.HeadPosition.z);

            for (int i = 0; i < JointCount; i++)
            {
                if (i == Neck || i == Head)
                    continue; // head chain below
                _positions[i] = pelvisTarget + yawRot * ((RecordingTemplate[i].Position - pelvisTemplate) * HeightScale);
                _rotations[i] = yawRot * RecordingTemplate[i].Rotation;
            }

            // ── head chain from the HMD ──
            _positions[Head] = frame.HeadPosition;
            _rotations[Head] = frame.HeadRotation;
            _positions[Neck] = frame.HeadPosition + frame.HeadRotation * new Vector3(0f, -NeckDropM, 0f);
            _rotations[Neck] = frame.HeadRotation;

            // ── arms: closed-form two-link solve per side (t08 grill
            // ruling 2/3/4), with the lost-side state machine ──
            var neckPos = _positions[Neck];
            var torsoForward = frame.HeadRotation * Vector3.forward;
            var leftState = SolveArm(
                left: true, frame.Left, frame.NowSeconds, frame.HeadPosition, frame.HeadRotation, neckPos, torsoForward,
                LeftArmJoints, LeftShoulder, LeftElbow, LeftWrist, LeftHand, LeftCollar,
                _frozenLeft, _frozenLeftRot, ref _hasFrozenLeft, ref _lastValidLeft,
                pelvisTarget, pelvisTemplate, yawRot);
            var rightState = SolveArm(
                left: false, frame.Right, frame.NowSeconds, frame.HeadPosition, frame.HeadRotation, neckPos, torsoForward,
                RightArmJoints, RightShoulder, RightElbow, RightWrist, RightHand, RightCollar,
                _frozenRight, _frozenRightRot, ref _hasFrozenRight, ref _lastValidRight,
                pelvisTarget, pelvisTemplate, yawRot);

            return new Result
            {
                Positions = _positions,
                Rotations = _rotations,
                LeftState = leftState,
                RightState = rightState,
            };
        }

        /// <summary>One side's arm: Live = closed-form solve, Held = frozen
        /// verbatim within the hold window, Static = root-placed template
        /// arm.</summary>
        private SideState SolveArm(
            bool left,
            HandInput hand,
            double now,
            Vector3 headPos,
            Quaternion headRot,
            Vector3 neckPos,
            Vector3 torsoForward,
            int[] joints,
            int shoulderIdx,
            int elbowIdx,
            int wristIdx,
            int handIdx,
            int collarIdx,
            Vector3[] frozenPos,
            Quaternion[] frozenRot,
            ref bool hasFrozen,
            ref double lastValid,
            Vector3 pelvisTarget,
            Vector3 pelvisTemplate,
            Quaternion yawRot)
        {
            bool live = hand.Valid &&
                        !float.IsNaN(hand.Position.x) && !float.IsNaN(hand.Position.y) && !float.IsNaN(hand.Position.z);
            if (!live)
            {
                if (hasFrozen && now - lastValid <= HoldSeconds)
                {
                    for (int k = 0; k < joints.Length; k++)
                    {
                        _positions[joints[k]] = frozenPos[k];
                        _rotations[joints[k]] = frozenRot[k];
                    }
                    return SideState.Held;
                }
                PlaceTemplateArm(joints, pelvisTarget, pelvisTemplate, yawRot);
                return SideState.Static;
            }

            lastValid = now;

            // ── closed-form two-link (grill ruling 2) ──
            var s = ShoulderAnchor(headPos, headRot, left);
            var dv = hand.Position - s;
            float d = dv.magnitude;
            var axis = d > 1e-6f ? dv / d : Vector3.down;
            float dMin = Mathf.Abs(UpperArmLength - ForearmLength) + ReachEpsilonM;
            float dMax = UpperArmLength + ForearmLength - ReachEpsilonM;
            float dEff = Mathf.Clamp(d, dMin, dMax); // ruling 3: clamp the target onto the reach sphere
            var wrist = s + axis * dEff;

            // Swivel: the hand orientation's fingers axis supplies the
            // forearm's RADIAL direction p̂ (⊥ shoulder→wrist); the axial
            // split comes from the distance geometry alone.
            var fDir = hand.Rotation * Vector3.right;
            var pRad = fDir - Vector3.Dot(fDir, axis) * axis;
            var fallback = RadialFallback(axis, torsoForward);
            var pBlend = pRad + PlaneNormalBlend * fallback; // continuity as pRad → 0
            var pHat = pBlend.sqrMagnitude > 1e-10f ? pBlend.normalized : fallback;

            // Law of cosines at the wrist joint: f̂ = α·axis + β·p̂ with
            // β = √(1−α²) keeps BOTH bone lengths exactly rigid.
            float alpha = Mathf.Clamp((dEff * dEff + ForearmLength * ForearmLength - UpperArmLength * UpperArmLength)
                / (2f * dEff * ForearmLength), -1f, 1f);
            float beta = Mathf.Sqrt(Mathf.Max(0f, 1f - alpha * alpha));
            var fHat = (alpha * axis + beta * pHat).normalized;
            var elbow = wrist - ForearmLength * fHat;
            var u = (elbow - s).normalized;

            // Flexion-plane normal, stabilized against the straight-arm
            // sign flip (synth _PLANE_NORMAL_BLEND).
            var normal = Vector3.Cross(u, fHat);
            var fallbackN = Vector3.Cross(u, Vector3.up);
            if (fallbackN.sqrMagnitude < 1e-10f)
                fallbackN = Vector3.Cross(u, Vector3.right);
            fallbackN = fallbackN.normalized;
            var nRef = (normal + PlaneNormalBlend * (UpperArmLength * ForearmLength) * fallbackN).normalized;

            // Segment orientations in the body convention (ported C_j).
            var upperFrame = AnatomicalFrame(u, nRef);
            var foreFrame = AnatomicalFrame(fHat, nRef);
            var shoulderConv = left ? LeftShoulderConvention : RightShoulderConvention;
            var elbowConv = left ? LeftElbowConvention : RightElbowConvention;
            var wristConv = left ? LeftWristPalmConvention : RightWristPalmConvention;
            var wristQuat = hand.Rotation * wristConv; // t20: measured C′ = P⁻¹·Q (hand orientation carries directly, ruling 6)

            var handPos = wrist + HandSegmentM * fDir;

            _positions[shoulderIdx] = s;
            _positions[elbowIdx] = elbow;
            _positions[wristIdx] = wrist;
            _positions[handIdx] = handPos;
            _rotations[shoulderIdx] = upperFrame * shoulderConv;
            _rotations[elbowIdx] = foreFrame * elbowConv;
            _rotations[wristIdx] = wristQuat;
            _rotations[handIdx] = wristQuat; // hand follows the wrist (synth precedent)

            // Collar: midpoint(neck, shoulder), minimal rotation from the
            // side's canonical lateral axis (synth _align_quat).
            float side = left ? -1f : 1f;
            _positions[collarIdx] = 0.5f * (neckPos + s);
            var collarDir = s - neckPos;
            _rotations[collarIdx] = collarDir.sqrMagnitude > 1e-10f
                ? Quaternion.FromToRotation(new Vector3(side, 0f, 0f), collarDir.normalized)
                : Quaternion.identity;

            for (int k = 0; k < joints.Length; k++)
            {
                frozenPos[k] = _positions[joints[k]];
                frozenRot[k] = _rotations[joints[k]];
            }
            hasFrozen = true;
            return SideState.Live;
        }

        /// <summary>A stable direction ⊥ the shoulder-wrist axis for the
        /// swivel fallback: first the torso forward (elbow biases back on a
        /// hanging arm), then world up (elbow sags on a forward reach),
        /// then world right.</summary>
        private static Vector3 RadialFallback(Vector3 axis, Vector3 torsoForward)
        {
            var fb = Reject(torsoForward, axis);
            if (fb.sqrMagnitude > 1e-10f)
                return fb.normalized;
            fb = Reject(Vector3.up, axis);
            if (fb.sqrMagnitude > 1e-10f)
                return fb.normalized;
            fb = Reject(Vector3.right, axis);
            return fb.sqrMagnitude > 1e-10f ? fb.normalized : Vector3.forward;
        }

        private static Vector3 Reject(Vector3 v, Vector3 axis)
        {
            return v - Vector3.Dot(v, axis) * axis;
        }

        /// <summary>Right-handed frame with x along <paramref name="axis"/>
        /// and y in the plane of (axis, <paramref name="reference"/>)
        /// (synth _anatomical_frame).</summary>
        private static Quaternion AnatomicalFrame(Vector3 axis, Vector3 reference)
        {
            var x = axis.normalized;
            var y = reference - Vector3.Dot(reference, x) * x;
            if (y.sqrMagnitude < 1e-12f)
            {
                y = Vector3.Cross(x, Vector3.up);
                if (y.sqrMagnitude < 1e-12f)
                    y = Vector3.Cross(x, Vector3.right);
                y = y.normalized;
            }
            else
            {
                y = y.normalized;
            }
            var z = Vector3.Cross(x, y);
            var m = default(Matrix4x4);
            m.SetColumn(0, x);
            m.SetColumn(1, y);
            m.SetColumn(2, z);
            m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return m.rotation;
        }

        private void PlaceTemplateArm(int[] joints, Vector3 pelvisTarget, Vector3 pelvisTemplate, Quaternion yawRot)
        {
            for (int k = 0; k < joints.Length; k++)
            {
                int i = joints[k];
                _positions[i] = pelvisTarget + yawRot * ((RecordingTemplate[i].Position - pelvisTemplate) * HeightScale);
                _rotations[i] = yawRot * RecordingTemplate[i].Rotation;
            }
        }
    }
}
