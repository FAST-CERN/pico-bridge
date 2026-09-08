#if UNITY_EDITOR
using System;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the in-app guided calibration flow (tracker-ik
    /// map t06): the timed guide state machine that renders pose guidance
    /// and countdowns on the panel (device round 2026-09-08 verdict: audio
    /// beats from the PC mis-cue the operator; guidance lives in-headset).
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT06CalibUiSmoke.Run -quit \
    ///     -logFile &lt;log&gt;
    ///
    /// Success marker: "[T06SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT06CalibUiSmoke
    {
        private const float Eps = 0.001f;

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T06SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T06SMOKE] ok: " + label);
            }

            void TickSeconds(float seconds)
            {
                float left = seconds;
                while (left > 0f)
                {
                    float step = Mathf.Min(0.25f, left);
                    TrackerCalibrationGuide.Tick(step);
                    left -= step;
                }
            }

            // Same synthetic world as the t05 smoke: a known rigid transform
            // puck<->hand, fake tracker frames, fixed head pose.
            var knownRot = Quaternion.AngleAxis(37f, new Vector3(1f, 0.4f, -0.25f).normalized);
            var knownTranslation = new Vector3(0.12f, -0.07f, 0.33f);
            var invKnown = Quaternion.Inverse(knownRot);
            var headPos = new Vector3(1.0f, 1.6f, -0.5f);
            var headRot = Quaternion.AngleAxis(23f, Vector3.up);

            var storePath = System.IO.Path.Combine(Application.temporaryCachePath, "t06-store-test.json");
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
            TrackerCalibrationGuide.ResetForTest();

            void PublishPose(int poseIndex)
            {
                for (int sideIdx = 0; sideIdx < 2; sideIdx++)
                {
                    string side = sideIdx == 0 ? "left" : "right";
                    CalibrationPoses.GetLocalPose(poseIndex, side, out var localPos, out var localRot);
                    var targetPos = headPos + headRot * localPos;
                    var puckPos = invKnown * (targetPos - knownTranslation);
                    var puckRot = invKnown * (headRot * localRot);
                    TrackerFrameCache.PublishValid(
                        side, sideIdx == 0 ? 7 : 8, puckPos, puckRot, TrackerFrameCache.Clock());
                }
            }

            // ── 1. guidance copy: ASCII instruction per pose ──
            Check(!string.IsNullOrEmpty(CalibrationPoses.Instruction(0)) &&
                  !string.IsNullOrEmpty(CalibrationPoses.Instruction(1)) &&
                  !string.IsNullOrEmpty(CalibrationPoses.Instruction(2)) &&
                  CalibrationPoses.Instruction(0) != CalibrationPoses.Instruction(1) &&
                  CalibrationPoses.Instruction(1) != CalibrationPoses.Instruction(2),
                "copy: instructions non-empty and pose-specific");
            bool allAscii = true;
            for (int i = 0; i < CalibrationPoses.Count; i++)
                foreach (var c in CalibrationPoses.Instruction(i))
                    if (c > 127)
                        allAscii = false;
            Check(allAscii, "copy: instructions are pure ASCII (panel font has no CJK glyphs)");

            // ── 2. Start -> Prep: text announces pose 1 with countdown ──
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Inactive,
                "guide: idle before Start");
            TrackerCalibrationGuide.Start();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Capturing,
                "guide: Start begins the session");
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Prep &&
                  TrackerCalibrationGuide.PoseIndex == 0,
                "guide: Start enters Prep at pose 0");
            string prepText = TrackerCalibrationGuide.StatusText();
            Check(prepText.Contains("1/3") && prepText.Contains("CHEST", StringComparison.OrdinalIgnoreCase),
                $"guide: Prep text names pose 1/3 chest ({prepText})");
            Check(TrackerCalibrationGuide.RemainingSeconds <= TrackerCalibrationGuide.PrepSeconds + Eps &&
                  TrackerCalibrationGuide.RemainingSeconds > TrackerCalibrationGuide.PrepSeconds - 1f,
                "guide: Prep counts down from PrepSeconds");

            // ── 3. Prep elapses -> Hold; no capture before Hold ends ──
            PublishPose(0);
            TickSeconds(TrackerCalibrationGuide.PrepSeconds);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Hold &&
                  TrackerCalibrationGuide.PoseIndex == 0,
                "guide: Prep elapses into Hold on the same pose");
            TickSeconds(TrackerCalibrationGuide.HoldSeconds - 1f);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Hold &&
                  TrackerCalibrationSession.NextPoseIndex == 0,
                "guide: no capture before the Hold window ends");
            Check(TrackerCalibrationGuide.IsUrgent,
                "guide: last seconds of Hold flag urgent (countdown color)");

            // ── 4. Hold ends -> capture fires -> next pose Prep ──
            TickSeconds(1f + Eps);
            Check(TrackerCalibrationSession.NextPoseIndex == 1 &&
                  TrackerCalibrationGuide.PoseIndex == 1 &&
                  TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Prep,
                "guide: Hold end captures and preps pose 2");

            // ── 5. capture miss -> Recapture retry, then success ──
            TickSeconds(TrackerCalibrationGuide.PrepSeconds + TrackerCalibrationGuide.HoldSeconds - 1f);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Hold &&
                  TrackerCalibrationGuide.PoseIndex == 1,
                "guide: pose 2 reaches its Hold window");
            TrackerFrameCache.PublishInvalid("left", 7, TrackerFrameCache.Clock());
            TickSeconds(1f + Eps);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Recapture &&
                  TrackerCalibrationSession.NextPoseIndex == 1,
                "guide: stale frame capture enters Recapture without filling the slot");
            Check(TrackerCalibrationGuide.StatusText().IndexOf("tracking", StringComparison.OrdinalIgnoreCase) >= 0,
                "guide: Recapture text says tracking lost");
            PublishPose(1);
            TickSeconds(TrackerCalibrationGuide.RecaptureSeconds + Eps);
            Check(TrackerCalibrationSession.NextPoseIndex == 2 &&
                  TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Prep,
                "guide: Recapture retries and advances to pose 3");

            // ── 6. third pose clean -> Committed verdict with residuals ──
            PublishPose(2);
            TickSeconds(TrackerCalibrationGuide.PrepSeconds + TrackerCalibrationGuide.HoldSeconds + Eps);
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Committed,
                "guide: third capture auto-solves and commits");
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Verdict,
                "guide: committed lands in Verdict");
            string verdictText = TrackerCalibrationGuide.StatusText();
            Check(verdictText.Contains("L") && verdictText.Contains("R") &&
                  verdictText.IndexOf("reject", StringComparison.OrdinalIgnoreCase) < 0,
                $"guide: committed verdict carries both-side residuals ({verdictText})");
            TickSeconds(TrackerCalibrationGuide.VerdictSeconds + Eps);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Inactive,
                "guide: verdict elapses back to Inactive");

            // ── 7. rejected path: verdict shows the gate reason ──
            TrackerCalibrationSession.ResetForTest();
            TrackerCalibrationSession.HeadSource = (out Vector3 hp, out Quaternion hr) =>
            {
                hp = headPos;
                hr = headRot;
                return true;
            };
            TrackerCalibrationGuide.ResetForTest();
            TrackerCalibrationGuide.Start();
            for (int pose = 0; pose < 3; pose++)
            {
                PublishPose(pose);
                // 10 cm off on the last pose: over the position gate.
                if (pose == 2)
                    TrackerFrameCache.PublishValid("left", 7,
                        new Vector3(0.10f, 0f, 0f), Quaternion.identity, TrackerFrameCache.Clock());
                TickSeconds(TrackerCalibrationGuide.PrepSeconds +
                            TrackerCalibrationGuide.HoldSeconds + Eps);
            }
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Rejected,
                "guide: over-gate data rejects");
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Verdict &&
                  TrackerCalibrationGuide.StatusText().IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0,
                $"guide: rejected verdict carries the reason ({TrackerCalibrationGuide.StatusText()})");
            TickSeconds(TrackerCalibrationGuide.VerdictSeconds + Eps);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Inactive,
                "guide: rejected verdict also elapses to Inactive");

            // ── 8. Abort mid-flow -> session Idle, guide Inactive ──
            TrackerCalibrationGuide.Start();
            TickSeconds(TrackerCalibrationGuide.PrepSeconds * 0.5f);
            TrackerCalibrationGuide.Abort();
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Inactive &&
                  TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Idle,
                "guide: Abort cancels flow and session");

            // ── 9. consecutive recapture misses give up with a verdict ──
            TrackerCalibrationGuide.Start();
            // Kill both sides so every capture in this flow misses (the
            // frozen test clock keeps stale frames "fresh", only the Valid
            // flag can fail them).
            TrackerFrameCache.PublishInvalid("left", 7, TrackerFrameCache.Clock());
            TrackerFrameCache.PublishInvalid("right", 8, TrackerFrameCache.Clock());
            TickSeconds(TrackerCalibrationGuide.PrepSeconds + TrackerCalibrationGuide.HoldSeconds);
            for (int miss = 0; miss < TrackerCalibrationGuide.MaxRecaptureAttempts; miss++)
                TickSeconds(TrackerCalibrationGuide.RecaptureSeconds + Eps);
            Check(TrackerCalibrationGuide.CurrentPhase == TrackerCalibrationGuide.Phase.Verdict &&
                  TrackerCalibrationGuide.StatusText().IndexOf("track", StringComparison.OrdinalIgnoreCase) >= 0,
                "guide: repeated misses give up into a tracking-lost verdict");

            TrackerCalibrationGuide.ResetForTest();
            TrackerCalibrationSession.ResetForTest();
            Debug.Log($"[T06SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
