#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the human-trim hand mapping (tracker-ik map
    /// t17). The solve era is gone (3-pose/9-DOF adjudicated dead on
    /// device 2026-09-09); the mapping is now a per-side operator trim:
    /// hand_rot = puck_rot · (yaw∘pitch∘roll in the puck-local frame),
    /// hand_pos = puck_pos − hand_rot · (0, level mm, 0) — the
    /// BodyMountCorrection level convention. Zero trim must be the
    /// identity mapping (hand ≡ puck), each axis must land on its Euler
    /// value, the composition must be right-multiplied (puck-local), and
    /// BOTH legacy store schemas (model-1 {R,t}, axyb-v1 {C,R_f,m}) must
    /// gate to defaults — their fields are not trim values. Run headless
    /// like the T05 smoke; marker "[T17SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT17HandTrimSmoke
    {
        // The store the device carried on 09-09 02:00 (verbatim model-1
        // schema fixture: R in qx..qw, t in tx..tz, |t| ~ 3.2 m).
        private const string LegacyModel1Store = @"{
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

        // A store written by the axyb-v1 era (solve fields, stamped model).
        private const string LegacyAxybStore = @"{
    ""model"": ""axyb-v1"",
    ""left"": {
        ""qx"": 0.1, ""qy"": 0.2, ""qz"": 0.3, ""qw"": 0.9,
        ""fx"": 0.0, ""fy"": 0.5, ""fz"": 0.5, ""fw"": 0.7,
        ""tx"": 0.08, ""ty"": -0.05, ""tz"": 0.10,
        ""positionRms"": 0.01, ""rotationRmsDeg"": 20.0,
        ""poseSet"": ""chest/side/front""
    },
    ""right"": {
        ""qx"": 0.1, ""qy"": 0.2, ""qz"": 0.3, ""qw"": 0.9,
        ""fx"": 0.0, ""fy"": 0.5, ""fz"": 0.5, ""fw"": 0.7,
        ""tx"": 0.08, ""ty"": -0.05, ""tz"": 0.10,
        ""positionRms"": 0.01, ""rotationRmsDeg"": 20.0,
        ""poseSet"": ""chest/side/front""
    }
}";

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T17SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T17SMOKE] ok: " + label);
            }

            var dir = Application.temporaryCachePath;
            var storePath = Path.Combine(dir, "t17-trim-test.json");
            TrackerHandCalibration.ResetForTest(storePath);

            var puckPos = new Vector3(0.3f, -0.2f, 0.5f);
            var puckRot = Quaternion.AngleAxis(37f, new Vector3(0.2f, 0.7f, 0.6f).normalized);

            // ── 1. zero trim = identity mapping; unknown side refused ──
            Check(TrackerHandCalibration.TryMap("left", puckPos, puckRot, out var pos0, out var rot0) &&
                  (pos0 - puckPos).magnitude < 1e-6f &&
                  Quaternion.Angle(rot0, puckRot) < 1e-3f,
                "zero trim maps hand ≡ puck (no uncalibrated state)");
            Check(!TrackerHandCalibration.TryMap("middle", puckPos, puckRot, out _, out _),
                "unknown side refused");

            // ── 2. per-axis Euler values ──
            void CheckAxis(float yaw, float pitch, float roll, Quaternion expected, string label)
            {
                TrackerHandCalibration.SetSide("left", yaw, pitch, roll, 0f);
                TrackerHandCalibration.TryMap("left", Vector3.zero, Quaternion.identity, out _, out var rot);
                Check(Quaternion.Angle(rot, expected) < 0.05f, label);
            }
            CheckAxis(90f, 0f, 0f, Quaternion.Euler(0f, 90f, 0f), "yaw 90 → Euler(0,90,0)");
            CheckAxis(0f, 90f, 0f, Quaternion.Euler(90f, 0f, 0f), "pitch 90 → Euler(90,0,0)");
            CheckAxis(0f, 0f, 90f, Quaternion.Euler(0f, 0f, 90f), "roll 90 → Euler(0,0,90)");
            CheckAxis(-35f, 20f, -10f,
                Quaternion.AngleAxis(-35f, Vector3.up) * Quaternion.AngleAxis(20f, Vector3.right) *
                Quaternion.AngleAxis(-10f, Vector3.forward),
                "yaw∘pitch∘roll composes in declaration order");

            // ── 3. trim is puck-LOCAL (right-multiplied) ──
            TrackerHandCalibration.SetSide("left", 90f, 0f, 0f, 0f);
            TrackerHandCalibration.TryMap("left", puckPos, puckRot, out var posL, out var rotL);
            Check(Quaternion.Angle(rotL, puckRot * Quaternion.Euler(0f, 90f, 0f)) < 0.05f,
                "trim right-multiplies: hand_rot = puck_rot · trim");

            // ── 4. level slides along the TRIMMED hand's up (minus sign,
            //    BodyMountCorrection convention) ──
            TrackerHandCalibration.SetSide("left", 0f, 0f, 0f, 25f);
            TrackerHandCalibration.TryMap("left", Vector3.zero, Quaternion.identity, out var posLevel, out var rotLevel);
            Check((posLevel - new Vector3(0f, -0.025f, 0f)).magnitude < 1e-6f &&
                  Quaternion.Angle(rotLevel, Quaternion.identity) < 1e-3f,
                "level 25mm on identity → pos slides −0.025 m, rot untouched");
            TrackerHandCalibration.SetSide("left", 90f, 0f, 0f, 25f);
            TrackerHandCalibration.TryMap("left", Vector3.zero, Quaternion.identity, out var posLevelYaw, out _);
            Check((posLevelYaw - -(Quaternion.Euler(0f, 90f, 0f) * new Vector3(0f, 0.025f, 0f))).magnitude < 1e-5f,
                "level slides along the trimmed orientation");

            // ── 5. persistence round-trip, sides independent ──
            TrackerHandCalibration.SetSide("left", 30f, -15f, 45f, 10f);
            TrackerHandCalibration.SetSide("right", -60f, 5f, -20f, -30f);
            var text = File.ReadAllText(storePath);
            Check(text.Contains("\"model\"") && text.Contains("trim-v1"),
                "SetSide stamps the trim-v1 schema");
            TrackerHandCalibration.LoadForTest(storePath);
            var left = TrackerHandCalibration.GetSide("left");
            var right = TrackerHandCalibration.GetSide("right");
            Check(left != null && Mathf.Abs(left.yaw - 30f) < 1e-4f && Mathf.Abs(left.pitch + 15f) < 1e-4f &&
                  Mathf.Abs(left.roll - 45f) < 1e-4f && Mathf.Abs(left.level - 10f) < 1e-4f,
                "left trim round-trips");
            Check(right != null && Mathf.Abs(right.yaw + 60f) < 1e-4f && Mathf.Abs(right.level + 30f) < 1e-4f,
                "right trim independent and round-trips");
            TrackerHandCalibration.TryMap("left", puckPos, puckRot, out var posRt, out var rotRt);
            Check((posRt - (puckPos - (puckRot *
                (Quaternion.AngleAxis(30f, Vector3.up) * Quaternion.AngleAxis(-15f, Vector3.right) *
                 Quaternion.AngleAxis(45f, Vector3.forward))) * new Vector3(0f, 0.010f, 0f))).magnitude < 1e-4f,
                "TryMap reproduces the persisted trim");

            // ── 6. schema gates: BOTH legacy schemas load as defaults ──
            var model1Path = Path.Combine(dir, "t17-legacy-model1.json");
            File.WriteAllText(model1Path, LegacyModel1Store);
            TrackerHandCalibration.LoadForTest(model1Path);
            var gated = TrackerHandCalibration.GetSide("left");
            Check(gated != null && gated.yaw == 0f && gated.pitch == 0f && gated.roll == 0f && gated.level == 0f,
                "model-1 legacy store gates to zero trim (no reinterpretation)");
            TrackerHandCalibration.TryMap("left", puckPos, puckRot, out var posG, out var rotG);
            Check((posG - puckPos).magnitude < 1e-6f && Quaternion.Angle(rotG, puckRot) < 1e-3f,
                "gated store maps hand ≡ puck");

            var axybPath = Path.Combine(dir, "t17-legacy-axyb.json");
            File.WriteAllText(axybPath, LegacyAxybStore);
            TrackerHandCalibration.LoadForTest(axybPath);
            var gated2 = TrackerHandCalibration.GetSide("right");
            Check(gated2 != null && gated2.yaw == 0f && gated2.level == 0f,
                "axyb-v1 legacy store also gates to zero trim");

            // ── 7. unknown future schema gates too ──
            var futurePath = Path.Combine(dir, "t17-future.json");
            File.WriteAllText(futurePath, text.Replace("trim-v1", "trim-v99"));
            TrackerHandCalibration.LoadForTest(futurePath);
            var gated3 = TrackerHandCalibration.GetSide("left");
            Check(gated3 != null && gated3.yaw == 0f,
                "unknown future schema gates to zero trim");

            Debug.Log($"[T17SMOKE] PASS ({checks} checks)");
        }
    }
}
#endif
