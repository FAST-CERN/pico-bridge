#if UNITY_EDITOR
using System;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the upper-body IK (tracker-ik map t08). All
    /// targets are synthetic, all thresholds are the ticket's grilled
    /// acceptance line (2026-09-09 04:28): wrist/hand position residual
    /// ≤5 mm, wrist orientation residual ≤1°, bone rigidity ≤0.1 mm,
    /// clamp = reach sphere ±0.1 mm straight-arm semantics, mirror
    /// positions ≤0.1 mm, single-frame budget ≤0.5 ms. The convention
    /// quats are duplicated here (from the Teleopit synth source, NOT from
    /// the solver's statics) so a swapped or typo'd table fails the
    /// golden-literal check. Run headless like the T17 smoke; marker
    /// "[T08SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT08IkSmoke
    {
        // Golden literals: Teleopit teleopit/inputs/tracker_arm_synth.py
        // _ARM_CONVENTION_QUATS (Unity xyzw), copied independently and
        // normalized (the table is 3-decimal rounded; raw norms sit off 1
        // by ~1e-3, which Unity's Angle does not normalize away).
        private static readonly Quaternion GoldLeftWrist = Quaternion.Normalize(
            new Quaternion(-0.089f, 0.134f, -0.980f, -0.121f));
        private static readonly Quaternion GoldRightWrist = Quaternion.Normalize(
            new Quaternion(-0.143f, -0.035f, 0.168f, 0.975f));
        private static readonly Quaternion GoldLeftShoulder = Quaternion.Normalize(
            new Quaternion(-0.047f, 0.052f, -0.997f, -0.025f));

        private const float PosTol = 5e-3f;      // 5 mm
        private const float RigidTol = 1e-4f;    // 0.1 mm
        private const float AngTol = 1f;         // 1 degree
        private const float MirrorTol = 1e-4f;   // 0.1 mm

        // Recording template pelvis/spine3 (first standing frame,
        // tracking_20260904_231338) — for root-placement expectations.
        private static readonly Vector3 TemplatePelvis = new Vector3(0.0370f, 0.9946f, 0.0997f);
        private static readonly Vector3 TemplateSpine3 = new Vector3(0.0182f, 1.3151f, 0.0677f);

        private static readonly Matrix4x4 MirrorX = new Matrix4x4(
            new Vector4(-1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(0f, 0f, 0f, 1f));

        private static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);
        private static Quaternion Mirror(Quaternion q) =>
            (MirrorX * Matrix4x4.Rotate(q) * MirrorX).rotation;

        /// <summary>Representable mirror of a palm-convention hand: the
        /// fingers and back axes are mirrored, green = cross(back, fingers)
        /// completes an orthonormal frame (the green-axis chirality is
        /// what the per-side conventions absorb).</summary>
        private static Quaternion MirrorPalm(Quaternion q)
        {
            var fingers = Mirror(q * Vector3.right);
            var back = Mirror(q * Vector3.forward);
            var green = Vector3.Cross(back, fingers).normalized;
            var m = default(Matrix4x4);
            m.SetColumn(0, fingers);
            m.SetColumn(1, green);
            m.SetColumn(2, back);
            m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return m.rotation;
        }

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T08SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T08SMOKE] ok: " + label);
            }

            bool Finite(Vector3 v) =>
                !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

            const float Height = 1.75f;
            float l1 = UpperBodyIkSolver.UpperArmFraction * Height;
            float l2 = UpperBodyIkSolver.ForearmFraction * Height;
            var solver = new UpperBodyIkSolver(Height);

            // A realistic standing head pose (HMD at eye height).
            var headPos = new Vector3(0f, 1.65f, 0f);
            var headRot = Quaternion.identity;
            var leftShoulder = UpperBodyIkSolver.ShoulderAnchor(headPos, headRot, true);
            var rightShoulder = UpperBodyIkSolver.ShoulderAnchor(headPos, headRot, false);

            UpperBodyIkSolver.FrameInput Frame(
                UpperBodyIkSolver.HandInput left, UpperBodyIkSolver.HandInput right, double now) =>
                new UpperBodyIkSolver.FrameInput
                {
                    HeadPosition = headPos,
                    HeadRotation = headRot,
                    Left = left,
                    Right = right,
                    NowSeconds = now,
                };

            var liveLeft = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = leftShoulder + new Vector3(0.3f, -0.2f, 0.9f).normalized * 0.45f,
                Rotation = Quaternion.identity,
            };
            var liveRight = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = rightShoulder + new Vector3(-0.3f, -0.2f, 0.9f).normalized * 0.45f,
                Rotation = Quaternion.identity,
            };

            // ── 1. config sanity ──────────────────────────────────
            Check(Mathf.Abs(solver.UpperArmLength - l1) < 1e-4f &&
                  Mathf.Abs(solver.ForearmLength - l2) < 1e-4f &&
                  Mathf.Abs(solver.HeightScale - Height / UpperBodyIkSolver.ReferenceHeightM) < 1e-5f,
                "bone lengths = fractions × height, scale = height/reference");

            // ── 2. root placement of the static template ──────────
            var res = solver.Solve(Frame(liveLeft, liveRight, 100.0));
            float pelvisY = TemplatePelvis.y * solver.HeightScale;
            Check((res.Positions[UpperBodyIkSolver.Pelvis] - new Vector3(headPos.x, pelvisY, headPos.z)).magnitude < 1e-4f,
                "pelvis = head horizontal projection + scaled template height");
            var spine3Expected = new Vector3(headPos.x, pelvisY, headPos.z) +
                (TemplateSpine3 - TemplatePelvis) * solver.HeightScale;
            Check((res.Positions[9] - spine3Expected).magnitude < 1e-4f,
                "template joints = pelvis + yaw∘(offset×scale) (identity yaw)");
            Check(Quaternion.Angle(res.Rotations[9], RecordingQuaternion(9)) < 0.05f,
                "template rotations = yaw∘template rotation (identity yaw)");

            // Head yaw rotates the template about the pelvis.
            var yawed = Quaternion.Euler(0f, 90f, 0f);
            var resYaw = solver.Solve(new UpperBodyIkSolver.FrameInput
            {
                HeadPosition = headPos,
                HeadRotation = yawed,
                Left = liveLeft,
                Right = liveRight,
                NowSeconds = 100.0,
            });
            Check(Quaternion.Angle(resYaw.Rotations[9], yawed * RecordingQuaternion(9)) < 0.05f,
                "head yaw rotates template rotations");
            var offsetRotated = Quaternion.Euler(0f, 90f, 0f) * ((TemplateSpine3 - TemplatePelvis) * solver.HeightScale);
            Check((resYaw.Positions[9] - (new Vector3(headPos.x, pelvisY, headPos.z) + offsetRotated)).magnitude < 1e-3f,
                "head yaw rotates template offsets about the pelvis");

            // ── 3. head/neck chain from the HMD ───────────────────
            var pitched = Quaternion.Euler(-20f, 30f, 5f);
            var resPitch = solver.Solve(new UpperBodyIkSolver.FrameInput
            {
                HeadPosition = headPos,
                HeadRotation = pitched,
                Left = liveLeft,
                Right = liveRight,
                NowSeconds = 100.0,
            });
            Check((resPitch.Positions[UpperBodyIkSolver.Head] - headPos).magnitude < 1e-6f &&
                  Quaternion.Angle(resPitch.Rotations[UpperBodyIkSolver.Head], pitched) < 0.01f,
                "head slot = HMD pose verbatim");
            var neckExpected = headPos + pitched * new Vector3(0f, -UpperBodyIkSolver.NeckDropM, 0f);
            Check((resPitch.Positions[UpperBodyIkSolver.Neck] - neckExpected).magnitude < 1e-5f &&
                  Quaternion.Angle(resPitch.Rotations[UpperBodyIkSolver.Neck], pitched) < 0.01f,
                "neck = head + rot·(0,−drop,0), neck rotation = head rotation");

            // ── 4. live FK round-trip (core) ──────────────────────
            res = solver.Solve(Frame(liveLeft, liveRight, 100.0));
            Check(res.LeftState == UpperBodyIkSolver.SideState.Live &&
                  res.RightState == UpperBodyIkSolver.SideState.Live,
                "valid hands → Live");
            Check((res.Positions[UpperBodyIkSolver.LeftWrist] - liveLeft.Position).magnitude < PosTol,
                "left wrist position = target (≤5mm)");
            Check((res.Positions[UpperBodyIkSolver.RightWrist] - liveRight.Position).magnitude < PosTol,
                "right wrist position = target (≤5mm)");
            Check(Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftShoulder], res.Positions[UpperBodyIkSolver.LeftElbow]) - l1) < RigidTol &&
                  Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftElbow], res.Positions[UpperBodyIkSolver.LeftWrist]) - l2) < RigidTol,
                "left bones rigid (|SE|−L1, |EW|−L2 ≤0.1mm)");
            Check(Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.RightShoulder], res.Positions[UpperBodyIkSolver.RightElbow]) - l1) < RigidTol &&
                  Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.RightElbow], res.Positions[UpperBodyIkSolver.RightWrist]) - l2) < RigidTol,
                "right bones rigid");
            var handExpected = res.Positions[UpperBodyIkSolver.LeftWrist] +
                UpperBodyIkSolver.HandSegmentM * (liveLeft.Rotation * Vector3.right);
            Check((res.Positions[UpperBodyIkSolver.LeftHand] - handExpected).magnitude < PosTol,
                "left hand = wrist + 0.09·rot·(+X fingers)");
            Check(Quaternion.Angle(res.Rotations[UpperBodyIkSolver.LeftWrist], liveLeft.Rotation * GoldLeftWrist) < AngTol,
                "left wrist rotation = R_hand∘C_wrist (golden literal, ≤1°)");
            Check(Quaternion.Angle(res.Rotations[UpperBodyIkSolver.RightWrist], liveRight.Rotation * GoldRightWrist) < AngTol,
                "right wrist rotation = R_hand∘C_wrist (golden literal, ≤1°)");
            Check(Quaternion.Angle(res.Rotations[UpperBodyIkSolver.LeftHand], res.Rotations[UpperBodyIkSolver.LeftWrist]) < 0.05f,
                "hand rotation follows wrist");

            // Shoulder anchor sanity (synth constants in the head frame).
            Check((res.Positions[UpperBodyIkSolver.LeftShoulder] - leftShoulder).magnitude < 1e-5f &&
                  (res.Positions[UpperBodyIkSolver.RightShoulder] - rightShoulder).magnitude < 1e-5f,
                "solved shoulders = head-frame anchors");

            // ── 5. rigidity + finiteness on assorted poses ────────
            Vector3[] axes =
            {
                Vector3.down,
                new Vector3(0.8f, 0.1f, -0.5f).normalized,
                new Vector3(-0.2f, 0.9f, 0.3f).normalized,
            };
            foreach (var axis in axes)
            {
                var hand = new UpperBodyIkSolver.HandInput
                {
                    Valid = true,
                    Position = leftShoulder + axis * 0.4f,
                    Rotation = Quaternion.AngleAxis(33f, new Vector3(0.2f, 0.7f, 0.6f).normalized),
                };
                var r = solver.Solve(Frame(hand, liveRight, 101.0));
                bool rigid = Mathf.Abs(Vector3.Distance(r.Positions[UpperBodyIkSolver.LeftShoulder], r.Positions[UpperBodyIkSolver.LeftElbow]) - l1) < RigidTol &&
                             Mathf.Abs(Vector3.Distance(r.Positions[UpperBodyIkSolver.LeftElbow], r.Positions[UpperBodyIkSolver.LeftWrist]) - l2) < RigidTol;
                bool finite = Finite(r.Positions[UpperBodyIkSolver.LeftElbow]) &&
                              Finite(r.Positions[UpperBodyIkSolver.LeftWrist]) &&
                              Finite(r.Positions[UpperBodyIkSolver.LeftHand]);
                Check(rigid && finite, $"assorted pose axis=({axis.x:0.0},{axis.y:0.0},{axis.z:0.0}) rigid+finite");
            }

            // ── 6. swivel responds to wrist orientation ───────────
            var swivelAxis = new Vector3(0.3f, -0.2f, 0.9f).normalized;
            var baseRot = Quaternion.identity;
            var rolled = Quaternion.AngleAxis(90f, swivelAxis) * baseRot;
            var elbowBase = ElbowOf(solver, Frame(
                new UpperBodyIkSolver.HandInput { Valid = true, Position = leftShoulder + swivelAxis * 0.45f, Rotation = baseRot }, liveRight, 102.0));
            var elbowRoll = ElbowOf(solver, Frame(
                new UpperBodyIkSolver.HandInput { Valid = true, Position = leftShoulder + swivelAxis * 0.45f, Rotation = rolled }, liveRight, 102.0));
            Check((elbowBase - elbowRoll).magnitude > 0.05f,
                "wrist roll about the arm axis moves the elbow (>5cm — swivel comes from orientation)");
            Check(Finite(elbowBase) && Finite(elbowRoll), "swivel poses finite");

            // ── 7. unreachable clamp ──────────────────────────────
            var farAxis = new Vector3(0f, 0.3f, 1f).normalized;
            var far = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = leftShoulder + farAxis * (l1 + l2 + 0.3f),
                Rotation = Quaternion.identity,
            };
            res = solver.Solve(Frame(far, liveRight, 103.0));
            var outVec = res.Positions[UpperBodyIkSolver.LeftWrist] - leftShoulder;
            Check(Mathf.Abs(outVec.magnitude - (l1 + l2 - UpperBodyIkSolver.ReachEpsilonM)) < 1e-4f &&
                  Vector3.Dot(outVec.normalized, farAxis) > 0.9999f,
                "over-reach clamps onto the reach sphere, direction kept (±0.1mm)");
            Check(Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftShoulder], res.Positions[UpperBodyIkSolver.LeftElbow]) - l1) < RigidTol &&
                  Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftElbow], res.Positions[UpperBodyIkSolver.LeftWrist]) - l2) < RigidTol,
                "clamped arm stays rigid (straight-arm semantics)");
            Check(Finite(res.Positions[UpperBodyIkSolver.LeftHand]) &&
                  Quaternion.Angle(res.Rotations[UpperBodyIkSolver.LeftWrist], Quaternion.identity * GoldLeftWrist) < AngTol,
                "clamped arm orientation unaffected");

            var close = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = leftShoulder + Vector3.down * 0.02f,
                Rotation = Quaternion.identity,
            };
            res = solver.Solve(Frame(close, liveRight, 104.0));
            outVec = res.Positions[UpperBodyIkSolver.LeftWrist] - leftShoulder;
            Check(Mathf.Abs(outVec.magnitude - (Mathf.Abs(l1 - l2) + UpperBodyIkSolver.ReachEpsilonM)) < 1e-4f &&
                  Finite(res.Positions[UpperBodyIkSolver.LeftElbow]),
                "folded target clamps out to the min-reach sphere");

            // Straight-arm boundary a hair inside the sphere.
            var straight = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = leftShoulder + farAxis * (l1 + l2 - 1e-3f),
                Rotation = Quaternion.identity,
            };
            res = solver.Solve(Frame(straight, liveRight, 105.0));
            Check(Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftShoulder], res.Positions[UpperBodyIkSolver.LeftElbow]) - l1) < RigidTol &&
                  Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftElbow], res.Positions[UpperBodyIkSolver.LeftWrist]) - l2) < RigidTol &&
                  Finite(res.Positions[UpperBodyIkSolver.LeftElbow]),
                "straight-arm boundary rigid and finite (β→0 degeneracy handled)");

            // Fingers along the arm axis (radial component ≈ 0 → fallback).
            var alongRot = Quaternion.FromToRotation(Vector3.right, farAxis);
            var along = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = leftShoulder + farAxis * 0.4f,
                Rotation = alongRot,
            };
            res = solver.Solve(Frame(along, liveRight, 106.0));
            Check(Finite(res.Positions[UpperBodyIkSolver.LeftElbow]) &&
                  Mathf.Abs(Vector3.Distance(res.Positions[UpperBodyIkSolver.LeftShoulder], res.Positions[UpperBodyIkSolver.LeftElbow]) - l1) < RigidTol,
                "fingers-along-axis pose uses the swivel fallback, rigid");

            // ── 8. mirror symmetry (positions; conventions are
            //        per-side golden-checked above) ─────────────────
            // The palm-convention hand frame is CHIRAL: the physical
            // mirror of a left-hand pose is not any rotation of the
            // same convention (det = −1). The representable mirror —
            // mirrored fingers/back axes with the right-handed
            // completion green = cross(back, fingers), i.e. a 180° roll
            // about the back axis — is what the solver should treat
            // positionally as the exact mirror.
            var rightMirrorRot = MirrorPalm(liveLeft.Rotation);
            var rightMirror = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = Mirror(liveLeft.Position),
                Rotation = rightMirrorRot,
            };
            res = solver.Solve(Frame(liveLeft, rightMirror, 107.0));
            int[] armLeft = { UpperBodyIkSolver.LeftCollar, UpperBodyIkSolver.LeftShoulder, UpperBodyIkSolver.LeftElbow, UpperBodyIkSolver.LeftWrist, UpperBodyIkSolver.LeftHand };
            int[] armRight = { UpperBodyIkSolver.RightCollar, UpperBodyIkSolver.RightShoulder, UpperBodyIkSolver.RightElbow, UpperBodyIkSolver.RightWrist, UpperBodyIkSolver.RightHand };
            bool mirrorOk = true;
            for (int k = 0; k < armLeft.Length; k++)
                mirrorOk &= (Mirror(res.Positions[armLeft[k]]) - res.Positions[armRight[k]]).magnitude < MirrorTol;
            Check(mirrorOk, "mirrored inputs → mirrored arm positions (≤0.1mm)");
            var expectShoulder = AnatomicalTimes(res, UpperBodyIkSolver.LeftShoulder, UpperBodyIkSolver.LeftElbow, l1, l2) * GoldLeftShoulder;
            Check(Quaternion.Angle(res.Rotations[UpperBodyIkSolver.LeftShoulder], expectShoulder) < AngTol,
                "left shoulder rotation = anatomical∘C_shoulder (golden literal)");

            // ── 9. lost-tracking state machine ────────────────────
            var fresh = new UpperBodyIkSolver(Height);
            var none = new UpperBodyIkSolver.HandInput { Valid = false };
            var r0 = fresh.Solve(Frame(none, none, 200.0));
            Check(r0.LeftState == UpperBodyIkSolver.SideState.Static &&
                  r0.RightState == UpperBodyIkSolver.SideState.Static,
                "never-seen sides → Static");
            float staticPelvisY = TemplatePelvis.y * fresh.HeightScale;
            var staticLeftShoulder = new Vector3(headPos.x, staticPelvisY, headPos.z) +
                (RecordingPosition(16) - TemplatePelvis) * fresh.HeightScale;
            Check((r0.Positions[UpperBodyIkSolver.LeftShoulder] - staticLeftShoulder).magnitude < 1e-4f,
                "static arm = root-placed template arm");

            var r1 = fresh.Solve(Frame(liveLeft, none, 201.0));
            Check(r1.LeftState == UpperBodyIkSolver.SideState.Live, "live after valid input");
            var frozenWrist = r1.Positions[UpperBodyIkSolver.LeftWrist];
            var frozenElbow = r1.Positions[UpperBodyIkSolver.LeftElbow];

            var r2 = fresh.Solve(Frame(none, none, 201.5));
            Check(r2.LeftState == UpperBodyIkSolver.SideState.Held &&
                  (r2.Positions[UpperBodyIkSolver.LeftWrist] - frozenWrist).magnitude < 1e-6f &&
                  (r2.Positions[UpperBodyIkSolver.LeftElbow] - frozenElbow).magnitude < 1e-6f,
                "lost <1s → Held (frozen verbatim)");

            var r3 = fresh.Solve(Frame(none, none, 203.0));
            Check(r3.LeftState == UpperBodyIkSolver.SideState.Static &&
                  (r3.Positions[UpperBodyIkSolver.LeftShoulder] - staticLeftShoulder).magnitude < 1e-4f,
                "lost >1s → Static (template arm, no divergence)");

            var r4 = fresh.Solve(Frame(liveLeft, none, 204.0));
            Check(r4.LeftState == UpperBodyIkSolver.SideState.Live &&
                  (r4.Positions[UpperBodyIkSolver.LeftWrist] - liveLeft.Position).magnitude < PosTol,
                "recovered side → Live and tracks again");

            // ── 10. height scaling ────────────────────────────────
            const float SmallHeight = 1.4f;
            var small = new UpperBodyIkSolver(SmallHeight);
            float smallL1 = UpperBodyIkSolver.UpperArmFraction * SmallHeight;
            float smallL2 = UpperBodyIkSolver.ForearmFraction * SmallHeight;
            var smallShoulder = UpperBodyIkSolver.ShoulderAnchor(headPos, headRot, true);
            var smallHand = new UpperBodyIkSolver.HandInput
            {
                Valid = true,
                Position = smallShoulder + new Vector3(0.3f, -0.2f, 0.9f).normalized * 0.35f,
                Rotation = Quaternion.identity,
            };
            var rs = small.Solve(Frame(smallHand, none, 300.0));
            Check(Mathf.Abs(Vector3.Distance(rs.Positions[UpperBodyIkSolver.LeftShoulder], rs.Positions[UpperBodyIkSolver.LeftElbow]) - smallL1) < RigidTol &&
                  Mathf.Abs(Vector3.Distance(rs.Positions[UpperBodyIkSolver.LeftElbow], rs.Positions[UpperBodyIkSolver.LeftWrist]) - smallL2) < RigidTol,
                "height 1.4 solver uses its own bone lengths");
            Check(Mathf.Abs(rs.Positions[UpperBodyIkSolver.Pelvis].y - TemplatePelvis.y * (SmallHeight / UpperBodyIkSolver.ReferenceHeightM)) < 1e-4f,
                "template height scales with operator height");

            // ── 11. per-frame time budget ─────────────────────────
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int iters = 2000;
            for (int i = 0; i < iters; i++)
            {
                var jitter = new Vector3(Mathf.Sin(i), Mathf.Cos(i * 0.7f), Mathf.Sin(i * 1.3f)).normalized;
                var hand = new UpperBodyIkSolver.HandInput
                {
                    Valid = true,
                    Position = leftShoulder + jitter * (0.2f + 0.3f * Mathf.Abs(Mathf.Sin(i * 0.37f))),
                    Rotation = Quaternion.AngleAxis(i % 360, jitter),
                };
                solver.Solve(Frame(hand, liveRight, 400.0 + i * 0.01));
            }
            sw.Stop();
            float msPerFrame = sw.ElapsedMilliseconds * 1000f / iters / 1000f;
            Debug.Log($"[T08SMOKE] timing: {msPerFrame:F5} ms/frame over {iters} solves");
            Check(msPerFrame < 0.5f, $"single-frame budget ≤0.5ms (measured {msPerFrame:F5}ms)");

            Debug.Log($"[T08SMOKE] PASS ({checks} checks)");
        }

        private static Vector3 ElbowOf(UpperBodyIkSolver solver, UpperBodyIkSolver.FrameInput frame)
        {
            return solver.Solve(frame).Positions[UpperBodyIkSolver.LeftElbow];
        }

        // Anatomical frame expectation helper mirroring the solver's
        // construction exactly (stabilized flexion-plane normal): rotation
        // with x = from→to, y = plane normal — used for the shoulder
        // golden check.
        private static Quaternion AnatomicalTimes(UpperBodyIkSolver.Result res, int from, int to, float l1, float l2)
        {
            var x = (res.Positions[to] - res.Positions[from]).normalized;
            var upper = (res.Positions[UpperBodyIkSolver.LeftElbow] - res.Positions[UpperBodyIkSolver.LeftShoulder]).normalized;
            var forearm = (res.Positions[UpperBodyIkSolver.LeftWrist] - res.Positions[UpperBodyIkSolver.LeftElbow]).normalized;
            var normal = Vector3.Cross(upper.normalized, forearm.normalized);
            var fallback = Vector3.Cross(upper.normalized, Vector3.up);
            if (fallback.sqrMagnitude < 1e-10f)
                fallback = Vector3.Cross(upper.normalized, Vector3.right);
            var nRef = (normal + UpperBodyIkSolver.PlaneNormalBlend * (l1 * l2) * fallback.normalized).normalized;
            var y = nRef - Vector3.Dot(nRef, x) * x;
            if (y.sqrMagnitude < 1e-12f)
                y = Vector3.up;
            y.Normalize();
            var z = Vector3.Cross(x, y);
            var m = default(Matrix4x4);
            m.SetColumn(0, x);
            m.SetColumn(1, y);
            m.SetColumn(2, z);
            m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            return m.rotation;
        }

        // Recording template accessors (duplicated literals so a template
        // typo fails here too).
        private static Quaternion RecordingQuaternion(int joint)
        {
            switch (joint)
            {
                case 9: return new Quaternion(-0.101145f, +0.228038f, +0.021769f, +0.968140f); // Spine3
                default: throw new ArgumentOutOfRangeException(nameof(joint));
            }
        }

        private static Vector3 RecordingPosition(int joint)
        {
            switch (joint)
            {
                case 16: return new Vector3(-0.1478f, +1.4278f, +0.1728f); // Left_Shoulder
                default: throw new ArgumentOutOfRangeException(nameof(joint));
            }
        }
    }
}
#endif
