#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the calibration store schema gate (tracker-ik
    /// map t13). Device root cause 09-09 02:00: a model-1 schema store
    /// (global {R,t}, no R_f fields, |t| ~ 3.2 m) loaded by the AX=YB code
    /// read R_f=(0,0,0,0) ~ identity and reinterpreted {R,t} as local
    /// {C,m} — the mapped hand flung ~3.2 m around the puck, with no
    /// calibration commit in between. The gate: a store whose schema
    /// version is missing or unrecognized loads as UNCALIBRATED (viz
    /// falls back to puck-only), never as a reinterpreted old solution.
    /// Run headless like the T05 smoke; marker "[T13SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT13SchemaGateSmoke
    {
        // The store the device actually carried on 09-09 02:00 (verbatim:
        // model-1 schema — R in qx..qw, t in tx..tz, |t| ~ 3.2 m, no
        // model field). The regression fixture.
        private const string LegacyStore = @"{
    ""left"": {
        ""qx"": 0.19172221422195435, ""qy"": -0.4732235074043274,
        ""qz"": -0.8267539739608765, ""qw"": 0.23617759346961976,
        ""tx"": -0.9792068600654602, ""ty"": 2.6119589805603029,
        ""tz"": -1.5018465518951417,
        ""positionRms"": 0.06166772544384003,
        ""rotationRmsDeg"": 118.09208679199219,
        ""poseSet"": ""chest/side/front""
    },
    ""right"": {
        ""qx"": -0.47898629307746889, ""qy"": 0.42110273241996767,
        ""qz"": 0.734391987323761, ""qw"": 0.23219196498394013,
        ""tx"": 0.9729218482971191, ""ty"": 2.9247307777404787,
        ""tz"": -0.7833889722824097,
        ""positionRms"": 0.06410227715969086,
        ""rotationRmsDeg"": 127.93173217773438,
        ""poseSet"": ""chest/side/front""
    }
}";

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T13SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T13SMOKE] ok: " + label);
            }

            var dir = Application.temporaryCachePath;

            // ── 1. the device defect: legacy schema must load UNCALIBRATED ──
            var legacyPath = Path.Combine(dir, "t13-legacy-store.json");
            File.WriteAllText(legacyPath, LegacyStore);
            TrackerHandCalibration.LoadForTest(legacyPath);
            Check(TrackerHandCalibration.GetSide("left") == null &&
                  TrackerHandCalibration.GetSide("right") == null,
                "legacy store loads as uncalibrated (no silent reinterpretation)");
            Check(!TrackerHandCalibration.TryMap("left", Vector3.zero, Quaternion.identity, out _, out _),
                "legacy store: TryMap false → viz falls back to puck-only");

            // ── 2. current-schema commit stamps the model field ──
            var storePath = Path.Combine(dir, "t13-store-test.json");
            TrackerHandCalibration.ResetForTest(storePath);
            var rf = Quaternion.AngleAxis(90f, Vector3.up);
            var c = Quaternion.AngleAxis(30f, Vector3.right);
            var m = new Vector3(0.10f, -0.05f, 0.20f);
            TrackerHandCalibration.Commit("left", new TrackerHandCalibration.SideParams
            {
                qx = c.x, qy = c.y, qz = c.z, qw = c.w,
                fx = rf.x, fy = rf.y, fz = rf.z, fw = rf.w,
                tx = m.x, ty = m.y, tz = m.z,
                positionRms = 0.01f,
                rotationRmsDeg = 20f,
                poseSet = "t13",
            });
            var text = File.ReadAllText(storePath);
            Check(text.Contains("\"model\"") && text.Contains("axyb"),
                "commit stamps a schema model field");

            // ── 3. current schema round-trips through load ──
            TrackerHandCalibration.LoadForTest(storePath);
            var puckRot = Quaternion.AngleAxis(25f, new Vector3(0.3f, 0.5f, 0.8f).normalized);
            var puckPos = new Vector3(0.3f, -0.2f, 0.5f);
            Check(TrackerHandCalibration.TryMap("left", puckPos, puckRot, out var pos, out var rot) &&
                  (pos - (rf * (puckPos + puckRot * m))).magnitude < 1e-4f &&
                  Quaternion.Angle(rot, rf * puckRot * c) < 0.1f,
                "current schema round-trips the AX=YB map");

            // ── 4. an unrecognized FUTURE schema also gates to uncalibrated ──
            var futurePath = Path.Combine(dir, "t13-future-store.json");
            File.WriteAllText(futurePath, text.Replace("axyb-v1", "axyb-v99"));
            TrackerHandCalibration.LoadForTest(futurePath);
            Check(TrackerHandCalibration.GetSide("left") == null,
                "unknown future schema also loads as uncalibrated");

            Debug.Log($"[T13SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
