#if UNITY_EDITOR
using System;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the Kabsch calibration (tracker-ik map t05).
    /// Round A: the rigid solver (Horn quaternion method) against synthetic
    /// goldens, and the three guided reference poses (head-relative, mirrored
    /// per side, non-collinear). Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT05CalibSmoke.Run -quit \
    ///     -logFile &lt;log&gt;
    ///
    /// Success marker: "[T05SMOKE] PASS". Tolerances are hand-chosen golden
    /// bounds (solver contract), pose VALUES are device-tuned defaults — the
    /// smoke asserts structure, not numbers.
    /// </summary>
    public static class PicoBridgeT05CalibSmoke
    {
        // Hand-chosen synthetic MOUNT (deterministic, no RNG): the constant
        // local puck-to-hand mount whose inverse the calibration recovers.
        private static readonly Quaternion KnownRot =
            Quaternion.AngleAxis(37f, new Vector3(1f, 0.4f, -0.25f).normalized);
        private static readonly Vector3 KnownTranslation = new Vector3(0.12f, -0.07f, 0.33f);
        private static readonly Quaternion KnownMountRot = KnownRot;         // C of the synthetic mount
        private static readonly Vector3 KnownMountOffset = KnownTranslation; // d, in the hand frame
        // AX=YB ground truth (2026-09-09 round 2): a LARGE constant frame
        // rotation between the head source and the cache (the device
        // reality the local-only model rejected at 0.6 m), plus the mount
        // offset m the solve must recover.
        private static readonly Quaternion KnownFrameRot =
            Quaternion.AngleAxis(153f, new Vector3(0.2f, 1f, -0.3f).normalized);
        private static readonly Vector3 KnownMountM = new Vector3(0.08f, -0.05f, 0.10f);

        // Non-collinear, well-spread "puck" positions (m, Unity frame).
        private static readonly Vector3[] KnownFrom =
        {
            new Vector3(0.30f, -0.35f, 0.45f),
            new Vector3(0.75f, -0.05f, 0.15f),
            new Vector3(0.18f, 0.10f, 0.62f),
        };

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T05SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T05SMOKE] ok: " + label);
            }

            float AngleDeg(Quaternion a, Quaternion b) =>
                Quaternion.Angle(a, b);

            // ── 1. Kabsch solver: known-transform roundtrip ──
            var knownTo = new Vector3[KnownFrom.Length];
            for (int i = 0; i < KnownFrom.Length; i++)
                knownTo[i] = KnownRot * KnownFrom[i] + KnownTranslation;

            bool solved = KabschSolver.Solve(
                KnownFrom, knownTo,
                out var rot, out var translation, out var rms);
            Check(solved, "kabsch: clean synthetic set solves");
            Check(AngleDeg(rot, KnownRot) < 0.01f,
                $"kabsch: rotation recovered (angle {AngleDeg(rot, KnownRot):0.0000}° < 0.01°)");
            Check((translation - KnownTranslation).magnitude < 1e-4f,
                $"kabsch: translation recovered (|Δt| {(translation - KnownTranslation).magnitude:0.000000} < 1e-4)");
            Check(rms < 1e-6f, $"kabsch: clean-set residual ~0 (rms {rms:0.00000000})");

            // ── 2. noisy roundtrip: residual tracks noise, rotation stays close ──
            var noisyTo = new Vector3[KnownFrom.Length];
            var offsets = new[]
            {
                new Vector3(0.010f, -0.004f, 0.008f),
                new Vector3(-0.006f, 0.009f, -0.011f),
                new Vector3(0.003f, -0.010f, 0.005f),
            };
            for (int i = 0; i < KnownFrom.Length; i++)
                noisyTo[i] = knownTo[i] + offsets[i];
            bool noisySolved = KabschSolver.Solve(
                KnownFrom, noisyTo, out var noisyRot, out var noisyT, out var noisyRms);
            Check(noisySolved, "kabsch: noisy set solves");
            Check(noisyRms > 0.002f && noisyRms < 0.02f,
                $"kabsch: noisy residual tracks the ~1cm noise (rms {noisyRms:0.0000})");
            Check(AngleDeg(noisyRot, KnownRot) < 3f,
                $"kabsch: noisy rotation within 3° ({AngleDeg(noisyRot, KnownRot):0.00}°) — 1cm noise on a 0.5m spread legitimately bends the fit");

            // ── 3. degeneracy: collinear / near-collinear rejection ──
            var collinear = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(0.2f, 0f, 0f),
            };
            Check(KabschSolver.IsDegenerate(collinear),
                "kabsch: exactly-collinear points flagged degenerate");
            var thin = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.40f, 0f, 0f),
                new Vector3(0.20f, 0.002f, 0f),
            };
            Check(KabschSolver.IsDegenerate(thin),
                "kabsch: near-collinear (thin triangle) flagged degenerate");
            Check(!KabschSolver.IsDegenerate(KnownFrom),
                "kabsch: the guided pose spread is non-degenerate");
            bool collinearSolved = KabschSolver.Solve(
                collinear, collinear, out _, out _, out _);
            Check(!collinearSolved, "kabsch: solve refuses collinear input");

            // ── 4. mirrored POINTS fit exactly — chirality is invisible to ──
            // point-only registration (a triangle and its mirror are
            // congruent, so a proper rotation aligns them perfectly). The
            // reflection degeneracy surfaces in the ORIENTATION residual,
            // which the session's rotation gate checks (Round B).
            var mirrored = new Vector3[KnownFrom.Length];
            for (int i = 0; i < KnownFrom.Length; i++)
                mirrored[i] = new Vector3(-knownTo[i].x, knownTo[i].y, knownTo[i].z);
            bool mirroredSolved = KabschSolver.Solve(
                KnownFrom, mirrored, out _, out _, out var mirroredRms);
            Check(mirroredSolved && mirroredRms < 1e-4f,
                $"kabsch: mirrored point set fits exactly (rms {mirroredRms:0.000000} — triangle congruence); the orientation gate catches reflections");

            // ── 5. guided pose set: head-relative, mirrored, non-collinear ──
            Check(CalibrationPoses.Count == 3, "poses: three reference poses");
            Check(CalibrationPoses.Name(0) == "chest" &&
                  CalibrationPoses.Name(1) == "side" &&
                  CalibrationPoses.Name(2) == "front",
                "poses: chest / side / front names (t06 guidance copy)");
            for (int i = 0; i < CalibrationPoses.Count; i++)
            {
                CalibrationPoses.GetLocalPose(i, "left", out var leftPos, out var leftRot);
                CalibrationPoses.GetLocalPose(i, "right", out var rightPos, out var rightRot);
                Check(Mathf.Abs(leftPos.x + rightPos.x) < 1e-5f &&
                      Mathf.Abs(leftPos.y - rightPos.y) < 1e-5f &&
                      Mathf.Abs(leftPos.z - rightPos.z) < 1e-5f,
                    $"poses[{i}]: left/right mirrored across the sagittal plane");
                Check(leftRot.eulerAngles.y >= -180f && leftRot.eulerAngles.y <= 180f,
                    $"poses[{i}]: left orientation is a valid rotation");
            }
            var leftTargets = new Vector3[CalibrationPoses.Count];
            for (int i = 0; i < CalibrationPoses.Count; i++)
            {
                CalibrationPoses.GetLocalPose(i, "left", out var pos, out _);
                leftTargets[i] = pos;
            }
            Check(!KabschSolver.IsDegenerate(leftTargets),
                "poses: the three left-hand targets are non-collinear");

            // ── 6. session + store: synthetic round-trip through a known ──
            // transform. Puck poses are the inverse-mapped targets, so the
            // solve must recover KnownRot/KnownTranslation exactly and
            // TryMap must land on the head-relative targets.
            var headPos = new Vector3(1.0f, 1.6f, -0.5f);
            var headRot = Quaternion.AngleAxis(23f, Vector3.up);

            // AX=YB ground truth (2026-09-09 round 2): the head source and
            // the cache frames differ by KnownFrameRot, and the puck sits on
            // the hand via a constant local mount. Generation inverts the
            // model target = Rf * (puck compose M):
            //   puck_rot = Rf^-1 * target_rot * C^-1
            //   puck_pos = Rf^-1 * target_pos - puck_rot * m
            // The solver must recover C = KnownMountRot, R_f = KnownFrameRot,
            // and m = KnownMountM. (Draft one generated pucks with a GLOBAL
            // transform - encoding the model bug the device round caught.)
            void PublishPose(int poseIndex, float posOffset, float rotOffsetDeg)
            {
                for (int sideIdx = 0; sideIdx < 2; sideIdx++)
                {
                    string side = sideIdx == 0 ? "left" : "right";
                    CalibrationPoses.GetLocalPose(poseIndex, side, out var localPos, out var localRot);
                    var targetPos = headPos + headRot * localPos;
                    var targetRot = headRot * localRot;
                    var puckRot = Quaternion.Inverse(KnownFrameRot) * targetRot *
                        Quaternion.Inverse(KnownMountRot) *
                        Quaternion.AngleAxis(rotOffsetDeg, Vector3.up);
                    var puckPos = Quaternion.Inverse(KnownFrameRot) * targetPos -
                        puckRot * KnownMountM +
                        new Vector3(posOffset, 0f, 0f);
                    TrackerFrameCache.PublishValid(
                        side, sideIdx == 0 ? 7 : 8, puckPos, puckRot, TrackerFrameCache.Clock());
                }
            }

            var storePath = System.IO.Path.Combine(Application.temporaryCachePath, "t05-store-test.json");
            TrackerHandCalibration.ResetForTest(storePath);
            TrackerCalibrationSession.ResetForTest();
            TrackerCalibrationSession.HeadSource = (out Vector3 hp, out Quaternion hr) =>
            {
                hp = headPos;
                hr = headRot;
                return true;
            };
            TrackerFrameCache.ResetForTest();
            TrackerFrameCache.Clock = () => 100f;

            Check(!TrackerHandCalibration.TryMap("left", KnownFrom[0], Quaternion.identity, out _, out _),
                "store: TryMap false with no calibration");

            TrackerCalibrationSession.Begin();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Capturing &&
                  TrackerCalibrationSession.NextPoseIndex == 0,
                "session: Begin enters Capturing at pose 0");

            PublishPose(0, 0f, 0f);
            TrackerFrameCache.PublishInvalid("right", 8, 100f); // right loses optical
            Check(!TrackerCalibrationSession.Capture(),
                "session: Capture refused when a side is optically invalid");
            Check(TrackerCalibrationSession.NextPoseIndex == 0,
                "session: refused capture fills no slot");

            PublishPose(0, 0f, 0f);
            Check(TrackerCalibrationSession.Capture(),
                "session: Capture accepted with both sides fresh+valid");
            Check(TrackerCalibrationSession.NextPoseIndex == 1,
                "session: slot filled, awaiting pose 1");

            PublishPose(1, 0f, 0f);
            Check(TrackerCalibrationSession.Capture(), "session: pose 1 captured");
            PublishPose(2, 0f, 0f);
            Check(TrackerCalibrationSession.Capture(), "session: pose 2 captured");
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Committed,
                "session: three clean captures auto-solve and Commit");

            CalibrationPoses.GetLocalPose(1, "left", out var lp1, out var lr1);
            var target1Pos = headPos + headRot * lp1;
            var target1Rot = headRot * lr1;
            var leftPuck1Rot = Quaternion.Inverse(KnownFrameRot) * target1Rot *
                Quaternion.Inverse(KnownMountRot);
            var leftPuck1 = Quaternion.Inverse(KnownFrameRot) * target1Pos -
                leftPuck1Rot * KnownMountM;
            Check(TrackerHandCalibration.TryMap("left", leftPuck1, leftPuck1Rot, out var mappedPos, out var mappedRot),
                "store: TryMap true after commit");
            Check((mappedPos - target1Pos).magnitude < 1e-3f &&
                  Quaternion.Angle(mappedRot, target1Rot) < 0.1f,
                "store: TryMap round-trips a mounted puck onto the hand target");
            var committedLeft = TrackerHandCalibration.GetSide("left");
            Check(committedLeft != null && Quaternion.Angle(
                      new Quaternion(committedLeft.qx, committedLeft.qy, committedLeft.qz, committedLeft.qw),
                      KnownMountRot) < 0.5f,
                "store: solved C matches the known mount");
            Check(committedLeft != null && Quaternion.Angle(
                      new Quaternion(committedLeft.fx, committedLeft.fy, committedLeft.fz, committedLeft.fw),
                      KnownFrameRot) < 0.5f,
                "store: solved R_f matches the known head-vs-cache frame rotation");
            Check(committedLeft != null &&
                  (new Vector3(committedLeft.tx, committedLeft.ty, committedLeft.tz) - KnownMountM).magnitude < 1e-3f,
                "store: solved m matches the known mount offset");

            // In-place hand rotation (the property the global model failed
            // on device): the hand rotates about its own centre, the puck
            // position swings with the mount offset, and the solved map
            // must still land on the (unchanged) hand position.
            var inPlace = Quaternion.AngleAxis(90f, Vector3.up);
            var followPuckRot = Quaternion.Inverse(KnownFrameRot) *
                (target1Rot * inPlace) * Quaternion.Inverse(KnownMountRot);
            var followPuckPos = Quaternion.Inverse(KnownFrameRot) * target1Pos -
                followPuckRot * KnownMountM;
            Check(TrackerHandCalibration.TryMap("left", followPuckPos, followPuckRot, out var followPos, out var followRot) &&
                  (followPos - target1Pos).magnitude < 1e-3f &&
                  Quaternion.Angle(followRot, target1Rot * inPlace) < 0.1f,
                "store: the map follows an in-place rotated hand (position AND rotation)");
            var committedRight = TrackerHandCalibration.GetSide("right");
            Check(committedRight != null && committedRight.positionRms < 1e-3f && committedRight.rotationRmsDeg < 0.1f,
                $"store: residuals recorded (pos {committedRight?.positionRms:0.000000} / rot {committedRight?.rotationRmsDeg:0.00}°)");

            // ── 7. persistence across a simulated restart ──
            TrackerHandCalibration.LoadForTest(storePath);
            Check(TrackerHandCalibration.TryMap("left", leftPuck1, leftPuck1Rot, out var reloadedPos, out _) &&
                  (reloadedPos - mappedPos).magnitude < 1e-6f,
                "store: calibration survives save/load round-trip");

            // ── 8. residual gates: position offset and rotation offset ──
            // both reject, clear the slots, and leave the committed store intact.
            TrackerCalibrationSession.ResetForTest();
            TrackerCalibrationSession.HeadSource = (out Vector3 hp, out Quaternion hr) =>
            {
                hp = headPos;
                hr = headRot;
                return true;
            };
            TrackerCalibrationSession.Begin();
            PublishPose(0, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(1, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(2, 0.60f, 0f); // 60cm on the last pose - big enough that the 3-pt Horn tilt cannot absorb it under the 0.10 gate
            Check(TrackerCalibrationSession.Capture(),
                "session: over-gate capture still fills the third slot (gate fires at solve)");
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Rejected &&
                  TrackerCalibrationSession.NextPoseIndex == 0,
                "session: position gate rejects and resets to pose 0");
            var afterReject = TrackerHandCalibration.GetSide("right");
            Check(afterReject != null && afterReject.positionRms == committedRight.positionRms &&
                  afterReject.rotationRmsDeg == committedRight.rotationRmsDeg,
                "session: rejected solve does not overwrite the committed store");

            // CONSISTENT rotation offset (every pose +90°): the local model
            // absorbs it into C — a different-but-valid mount, so the solve
            // commits and the mapped hand stays glued to the puck. This is
            // the model check the first (global) formulation failed on
            // device: rotate the hand in place, the map must follow.
            PublishPose(0, 0f, 90f);
            TrackerCalibrationSession.Capture();
            PublishPose(1, 0f, 90f);
            TrackerCalibrationSession.Capture();
            PublishPose(2, 0f, 90f);
            TrackerCalibrationSession.Capture();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Committed,
                "session: consistent mount-rotation offset absorbs into C (local model)");
            var rotatedLeft = TrackerHandCalibration.GetSide("left");
            Check(rotatedLeft != null, "session: rotated-mount round commits (store replaced)");

            PublishPose(0, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(1, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(2, 0f, 0f);
            TrackerCalibrationSession.Capture();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Committed,
                "session: clean re-capture after rejection commits");

            // ── 9. abort ──
            TrackerCalibrationSession.ResetForTest();
            TrackerCalibrationSession.HeadSource = (out Vector3 hp, out Quaternion hr) =>
            {
                hp = headPos;
                hr = headRot;
                return true;
            };
            TrackerCalibrationSession.Begin();
            PublishPose(0, 0f, 0f);
            TrackerCalibrationSession.Capture();
            TrackerCalibrationSession.Abort();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Idle &&
                  TrackerCalibrationSession.NextPoseIndex == 0,
                "session: Abort returns to Idle and clears slots");

            // ── 10. BridgeControl routing (receiver/dev-script entry) ──
            var managerObject = new GameObject("T05SmokeManager");
            try
            {
                var manager = managerObject.AddComponent<PicoBridgeManager>();
                const string beginJson =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_calibration\", " +
                    "\"payload\": {\"action\": \"begin\"}}";
                InvokePrivate(manager, "HandleBridgeControl", beginJson);
                Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Capturing,
                    "routing: set_calibration begin enters Capturing");

                PublishPose(0, 0f, 0f);
                const string captureJson =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_calibration\", " +
                    "\"payload\": {\"action\": \"capture\"}}";
                InvokePrivate(manager, "HandleBridgeControl", captureJson);
                Check(TrackerCalibrationSession.NextPoseIndex == 1,
                    "routing: set_calibration capture fills a slot");

                const string abortJson =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_calibration\", " +
                    "\"payload\": {\"action\": \"abort\"}}";
                InvokePrivate(manager, "HandleBridgeControl", abortJson);
                Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Idle,
                    "routing: set_calibration abort returns to Idle");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(managerObject);
            }

            TrackerCalibrationSession.ResetForTest();
            TrackerHandCalibration.ResetForTest(storePath);
            TrackerFrameCache.ResetForTest();
            TrackerFrameCache.Clock = null;

            Debug.Log($"[T05SMOKE] PASS ({checks} checks)");
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T05SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }
    }
}
#endif
