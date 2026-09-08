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
    /// map t14 infra). The solve session that fed it is retired (t17
    /// human-trim); what survives is the append-only channel itself —
    /// chatty eats Debug.Log on device, so anything that must survive a
    /// round lands in this file. The smoke drives AppendSample directly
    /// (the future producer's contract) and replays the file back: line
    /// count, per-line parse, field fidelity (poses verbatim, session
    /// grouping, kind label). Run headless like the T05 smoke; marker
    /// "[T14SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT14SampleLogSmoke
    {
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

            var logPath = Path.Combine(Application.temporaryCachePath, "t14-samples-test.jsonl");
            TrackerCalibrationSampleLog.ResetForTest(logPath);

            var headPos = new Vector3(1.0f, 1.6f, -0.5f);
            var headRot = Quaternion.AngleAxis(23f, Vector3.up);
            var puckPos = new Vector3(0.3f, -0.2f, 0.5f);
            var puckRot = Quaternion.AngleAxis(37f, new Vector3(0.2f, 0.7f, 0.6f).normalized);
            var targetPos = headPos + headRot * new Vector3(0.2f, -0.3f, 0.4f);
            var targetRot = headRot * Quaternion.AngleAxis(30f, Vector3.right);

            for (int i = 0; i < 3; i++)
                TrackerCalibrationSampleLog.AppendSample(
                    1, $"pose{i}",
                    puckPos, puckRot, targetPos, targetRot, headPos, headRot);
            for (int i = 0; i < 2; i++)
                TrackerCalibrationSampleLog.AppendSample(
                    2, $"probe{i}",
                    puckPos, puckRot, targetPos, targetRot, headPos, headRot);

            var lines = File.ReadAllLines(logPath)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(lines.Length == 5,
                $"append-only: 5 appended lines (got {lines.Length})");

            var samples = lines
                .Select(l => JsonUtility.FromJson<TrackerCalibrationSampleLog.SampleLine>(l))
                .ToArray();
            Check(samples.Count(s => s.type == "sample") == 5,
                "every line parses as a sample");
            Check(samples.Count(s => s.session == 1) == 3 && samples.Count(s => s.session == 2) == 2,
                "session ids group lines");
            Check(samples.All(s => s.kind.StartsWith("pose") && s.session == 1 ||
                                   s.kind.StartsWith("probe") && s.session == 2),
                "kind labels survive");

            var s0 = samples[0];
            Check((new Vector3(s0.puck.px, s0.puck.py, s0.puck.pz) - puckPos).magnitude < 1e-5f &&
                  Quaternion.Angle(new Quaternion(s0.puck.qx, s0.puck.qy, s0.puck.qz, s0.puck.qw), puckRot) < 0.01f,
                "puck pose verbatim");
            Check((new Vector3(s0.target.px, s0.target.py, s0.target.pz) - targetPos).magnitude < 1e-5f &&
                  Quaternion.Angle(new Quaternion(s0.target.qx, s0.target.qy, s0.target.qz, s0.target.qw), targetRot) < 0.01f,
                "target pose verbatim");
            Check((new Vector3(s0.head.px, s0.head.py, s0.head.pz) - headPos).magnitude < 1e-5f,
                "head pose verbatim");
            Check(!string.IsNullOrEmpty(s0.t),
                "wall-clock timestamp present");

            // Appending after a ResetForTest truncates (scratch isolation).
            TrackerCalibrationSampleLog.ResetForTest(logPath);
            TrackerCalibrationSampleLog.AppendSample(
                3, "solo", puckPos, puckRot, targetPos, targetRot, headPos, headRot);
            lines = File.ReadAllLines(logPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(lines.Length == 1,
                "ResetForTest truncates the scratch file");

            Debug.Log($"[T14SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
