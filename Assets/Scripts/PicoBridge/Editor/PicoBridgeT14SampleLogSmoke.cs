#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the calibration sample JSONL log (tracker-ik
    /// map t14). On device chatty eats every Debug.Log line, so the JSONL
    /// file is a round's only surviving record: this smoke drives two
    /// synthetic sessions (clean commit + over-gate reject) through the
    /// real session/store pipeline and replays the file back — every
    /// sample line must carry both sides' raw puck/target poses plus the
    /// head pose the target was composed from, and each solve must leave
    /// an event line (solutions on commit, reason on reject). Run headless
    /// like the T05 smoke; marker "[T14SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT14SampleLogSmoke
    {
        // Same synthetic AX=YB world as the T05 smoke: a large constant
        // frame rotation + a local mount the solver must recover.
        private static readonly Quaternion KnownFrameRot =
            Quaternion.AngleAxis(153f, new Vector3(0.2f, 1f, -0.3f).normalized);
        private static readonly Quaternion KnownMountRot =
            Quaternion.AngleAxis(37f, new Vector3(1f, 0.4f, -0.25f).normalized);
        private static readonly Vector3 KnownMountM = new Vector3(0.08f, -0.05f, 0.10f);

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T14SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T14SMOKE] ok: " + label);
            }

            var headPos = new Vector3(1.0f, 1.6f, -0.5f);
            var headRot = Quaternion.AngleAxis(23f, Vector3.up);

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

            var logPath = Path.Combine(Application.temporaryCachePath, "t14-samples-test.jsonl");
            var storePath = Path.Combine(Application.temporaryCachePath, "t14-store-test.json");
            TrackerCalibrationSampleLog.ResetForTest(logPath);
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

            // Round 1: clean → commit (6 samples + 1 committed event).
            TrackerCalibrationSession.Begin();
            PublishPose(0, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(1, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(2, 0f, 0f);
            TrackerCalibrationSession.Capture();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Committed,
                "round 1 commits");

            // Round 2: last pose 60 cm off → reject (6 samples + 1 rejected).
            TrackerCalibrationSession.Begin();
            PublishPose(0, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(1, 0f, 0f);
            TrackerCalibrationSession.Capture();
            PublishPose(2, 0.60f, 0f);
            TrackerCalibrationSession.Capture();
            Check(TrackerCalibrationSession.CurrentState == TrackerCalibrationSession.State.Rejected,
                "round 2 rejects at the position gate");

            // ── replay the file back ──
            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(lines.Length == 14,
                $"jsonl holds both rounds (6+1 + 6+1 = 14 lines), got {lines.Length}");

            var parsed = lines.Select(l => new
            {
                Sample = JsonUtility.FromJson<TrackerCalibrationSampleLog.SampleLine>(l),
                Event = JsonUtility.FromJson<TrackerCalibrationSampleLog.EventLine>(l),
            }).ToArray();
            var samples = parsed.Select(p => p.Sample).Where(s => s.type == "sample").ToArray();
            var events = parsed.Select(p => p.Event)
                .Where(e => e.type == "committed" || e.type == "rejected").ToArray();
            Check(samples.Length == 12, "12 sample lines (3 poses × 2 sides × 2 rounds)");
            Check(events.Length == 2, "2 event lines (committed + rejected)");
            Check(events[0].type == "committed" && events[1].type == "rejected",
                "events land in round order");

            // Field completeness: a sample carries the raw head pose and
            // the composed target (target = head ∘ literal).
            var s0 = samples.First(s => s.side == "left" && s.poseIndex == 0 && s.session == events[0].session);
            CalibrationPoses.GetLocalPose(0, "left", out var localPos0, out var localRot0);
            var wantPos = headPos + headRot * localPos0;
            var wantRot = headRot * localRot0;
            Check((new Vector3(s0.target.px, s0.target.py, s0.target.pz) - wantPos).magnitude < 1e-4f &&
                  Quaternion.Angle(new Quaternion(s0.target.qx, s0.target.qy, s0.target.qz, s0.target.qw), wantRot) < 0.1f,
                "sample target = head ∘ pose literal");
            Check((new Vector3(s0.head.px, s0.head.py, s0.head.pz) - headPos).magnitude < 1e-4f &&
                  Quaternion.Angle(new Quaternion(s0.head.qx, s0.head.qy, s0.head.qz, s0.head.qw), headRot) < 0.1f,
                "sample carries the raw head pose");
            Check(samples.GroupBy(s => s.poseIndex).Count() == 3 &&
                  samples.All(s => s.side == "left" || s.side == "right"),
                "samples cover poses 0-2 for both sides");

            // Committed event carries both sides' full solutions.
            var committed = events[0];
            Check(committed.left != null && committed.right != null,
                "committed event carries both sides");
            Check(Quaternion.Angle(
                      new Quaternion(committed.left.rf[0], committed.left.rf[1], committed.left.rf[2], committed.left.rf[3]),
                      KnownFrameRot) < 0.5f &&
                  (new Vector3(committed.left.m[0], committed.left.m[1], committed.left.m[2]) - KnownMountM).magnitude < 1e-3f,
                "committed solution matches the synthetic ground truth (R_f + m)");

            // Rejected event groups with round 2's samples and names the gate.
            var rejected = events[1];
            Check(rejected.session != committed.session &&
                  samples.Where(s => s.session == rejected.session).Count() == 6,
                "round-2 samples group under the rejected session id");
            Check(rejected.reason.Contains("position rms"),
                $"rejected event carries the gate reason ('{rejected.reason}')");

            Debug.Log($"[T14SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
