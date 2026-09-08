#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the head-source frame probe (tracker-ik map
    /// t15). Injects synthetic sources: the "XR camera" head sweeps
    /// through positions and rotations (a swept rotation is what makes a
    /// constant frame relation identifiable — the collinearity lesson
    /// from the device rounds), the "PXR native" head is a KNOWN constant
    /// frame relation F,T of it. A full probe window must land one JSONL
    /// line per accepted tick with delta = F every line, plus a summary
    /// (spread ~0, residual = |T|). Fault paths: a failing source skips
    /// ticks without crashing; a window with no sources summarizes n=0.
    /// Run headless like the T05 smoke; marker "[T15SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT15HeadProbeSmoke
    {
        private static readonly Quaternion KnownF =
            Quaternion.AngleAxis(120f, new Vector3(0.3f, 0.5f, 0.8f).normalized);
        private static readonly Vector3 KnownT = new Vector3(0.8f, -1.2f, 0.4f);

        [Serializable]
        private class PoseJson
        {
            public float px, py, pz, qx, qy, qz, qw;
        }

        [Serializable]
        private class SampleJson
        {
            public string type = "";
            public float elapsed;
            public PoseJson unity;
            public PoseJson native;
            public PoseJson delta;
        }

        [Serializable]
        private class SummaryJson
        {
            public string type = "";
            public int n;
            public float durS;
            public float angMeanDeg, angSpreadDeg, offResMean, offResMax;
        }

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T15SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T15SMOKE] ok: " + label);
            }

            var path = Path.Combine(Application.temporaryCachePath, "t15-probe-test.jsonl");
            HeadFrameProbe.ResetForTest(path);

            double now = 0.0;
            HeadFrameProbe.Clock = () => now;
            int tick = 0;
            int unityFailUntil = -1; // inclusive tick index the unity source fails through
            Vector3 unityPos = default;
            Quaternion unityRot = Quaternion.identity;

            HeadFrameProbe.UnitySource = (out Vector3 p, out Quaternion r) =>
            {
                if (tick <= unityFailUntil)
                {
                    p = default;
                    r = default;
                    return false;
                }
                p = unityPos;
                r = unityRot;
                return true;
            };
            HeadFrameProbe.NativeSource = (out Vector3 p, out Quaternion r) =>
            {
                p = KnownF * unityPos + KnownT;
                r = KnownF * unityRot;
                return true;
            };

            void Sweep(int k)
            {
                unityPos = new Vector3(
                    0.4f * Mathf.Sin(k * 0.3f),
                    1.6f + 0.1f * Mathf.Sin(k * 0.17f),
                    -0.5f + 0.3f * Mathf.Cos(k * 0.3f));
                unityRot = Quaternion.AngleAxis(k * 7f, new Vector3(0.2f, 0.7f, 0.6f).normalized);
            }

            // ── window 1: clean 0.5 s at 72 Hz ──
            HeadFrameProbe.Begin(0.5f);
            Check(HeadFrameProbe.IsRunning && HeadFrameProbe.SampleCount == 0,
                "Begin opens a window at zero samples");
            for (int k = 0; k < 60; k++)
            {
                tick = k;
                Sweep(k);
                now = k / 72.0;
                HeadFrameProbe.Tick();
            }
            Check(!HeadFrameProbe.IsRunning, "window auto-closes at the deadline");
            var n1 = HeadFrameProbe.SampleCount;
            Check(n1 >= 30 && n1 <= 38,
                $"deadline lands at ~36 ticks (got {n1})");

            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(lines.Length == n1 + 1,
                $"jsonl = {n1} samples + 1 summary (got {lines.Length})");
            var samples = lines.Select(l => JsonUtility.FromJson<SampleJson>(l))
                .Where(s => s.type == "probe_sample").ToArray();
            var summaries = lines.Select(l => JsonUtility.FromJson<SummaryJson>(l))
                .Where(s => s.type == "probe_summary").ToArray();
            Check(samples.Length == n1 && summaries.Length == 1,
                "line types parse back cleanly");

            foreach (var s in samples)
            {
                var dq = new Quaternion(s.delta.qx, s.delta.qy, s.delta.qz, s.delta.qw);
                if (Quaternion.Angle(dq, KnownF) > 0.1f)
                {
                    Check(false, $"sample delta recovers the constant F (line elapsed {s.elapsed})");
                    break;
                }
            }
            Check(true, "every sample's delta rot = the constant frame F");

            foreach (var s in samples)
            {
                var uq = new Quaternion(s.unity.qx, s.unity.qy, s.unity.qz, s.unity.qw);
                var nq = new Quaternion(s.native.qx, s.native.qy, s.native.qz, s.native.qw);
                var up = new Vector3(s.unity.px, s.unity.py, s.unity.pz);
                var np = new Vector3(s.native.px, s.native.py, s.native.pz);
                if (Quaternion.Angle(nq, KnownF * uq) > 0.1f ||
                    (np - (KnownF * up + KnownT)).magnitude > 1e-4f)
                {
                    Check(false, $"sample raw poses match native = F ∘ unity (line elapsed {s.elapsed})");
                    break;
                }
            }
            Check(true, "sample raw poses record both sources verbatim");

            var sum = summaries[0];
            Check(sum.n == n1 && sum.angSpreadDeg < 0.05f &&
                  Mathf.Abs(sum.offResMean - KnownT.magnitude) < 1e-3f,
                $"summary: n={sum.n} angSpread={sum.angSpreadDeg:0.0000}° offResMean={sum.offResMean:0.000000} (|T|={KnownT.magnitude:0.000000})");

            // ── window 2: unity source fails the first 5 ticks ──
            now = 100.0;
            HeadFrameProbe.Begin(0.2f);
            unityFailUntil = 4;
            for (int k = 0; k < 30; k++)
            {
                tick = k;
                Sweep(100 + k);
                now = 100.0 + k / 72.0;
                HeadFrameProbe.Tick();
            }
            unityFailUntil = -1;
            var n2 = HeadFrameProbe.SampleCount;
            Check(!HeadFrameProbe.IsRunning && n2 >= 9 && n2 <= 14,
                $"failing source skips its ticks without crashing (got {n2})");
            lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(lines.Length == n1 + 1 + n2 + 1,
                $"file appends: {n1}+1 then {n2}+1 (got {lines.Length})");
            summaries = lines.Select(l => JsonUtility.FromJson<SummaryJson>(l))
                .Where(s => s.type == "probe_summary").ToArray();
            Check(summaries.Length == 2 && summaries[1].n == n2,
                "second window gets its own summary");

            // ── window 3: no sources wired ──
            HeadFrameProbe.ResetForTest(path);
            HeadFrameProbe.UnitySource = null;
            HeadFrameProbe.NativeSource = null;
            HeadFrameProbe.Begin(0.1f);
            for (int k = 0; k < 15; k++)
            {
                now = 200.0 + k / 72.0;
                HeadFrameProbe.Tick();
            }
            lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Check(!HeadFrameProbe.IsRunning && lines.Length == 1 &&
                  JsonUtility.FromJson<SummaryJson>(lines[0]).n == 0,
                "no sources: window times out with an n=0 summary, no crash");

            Debug.Log($"[T15SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
