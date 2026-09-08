#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the app-side mount correction (bodytrack-deploy
    /// t07): correction math, role mapping, persistence round-trip,
    /// receiver-format BridgeControl parsing, and editor-mock parity. Run
    /// headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeMountCorrectionSmoke.Run -logFile &lt;log&gt;
    ///
    /// Success marker in the log: "[MOUNTSMOKE] PASS". Any failed check throws,
    /// which surfaces as an executeMethod exception in the log.
    /// </summary>
    public static class PicoBridgeMountCorrectionSmoke
    {
        private const int LeftWristRole = 20;
        private const int RightWristRole = 21;
        private const int LeftHandRole = 22;
        private const int ElbowRole = 19;

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[MOUNTSMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[MOUNTSMOKE] ok: " + label);
            }

            string scratch = Path.Combine(Path.GetTempPath(), "pico_bridge_mount_smoke.json");
            try
            {
                if (File.Exists(scratch))
                    File.Delete(scratch);

                // ── defaults: enabled, identity, zero translation ──
                Tracking.BodyMountCorrection.ResetForTest(scratch);
                Check(Tracking.BodyMountCorrection.Enabled, "default: correction enabled");
                var pos = new Vector3(0.42f, 1.05f, -0.12f);
                var rot = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                var posIn = pos;
                var rotIn = rot;
                Tracking.BodyMountCorrection.Apply(LeftWristRole, ref pos, ref rot);
                Check((pos - posIn).magnitude < 1e-6f && Mathf.Abs(Quaternion.Dot(rot, rotIn) - 1f) < 1e-4f,
                    "default zero params: identity pose preserved");

                // ── disable toggle (t10 semantics) ──
                Tracking.BodyMountCorrection.SetEnabled(false);
                Check(!Tracking.BodyMountCorrection.Apply(LeftWristRole, ref pos, ref rot),
                    "disabled: Apply returns false");
                Tracking.BodyMountCorrection.SetEnabled(true);

                // ── known-value math: q' = q * yaw-twist; level = axial
                // slide in MILLIMETRES (d7bfb22 UX rework semantics — the
                // panel knob slides the block, it does not pitch it) ──
                Tracking.BodyMountCorrection.SetSide("left", yaw: 90f, level: 0f);
                rot = Quaternion.identity;
                pos = Vector3.zero;
                Tracking.BodyMountCorrection.Apply(LeftWristRole, ref pos, ref rot);
                var expectedYaw = Quaternion.Euler(0f, 90f, 0f);
                Check(Mathf.Abs(Quaternion.Dot(rot, expectedYaw) - 1f) < 1e-4f,
                    "yaw 90 on identity: q' = Euler(0,90,0)");

                Tracking.BodyMountCorrection.SetSide("left", yaw: 0f, level: 90f);
                rot = Quaternion.identity;
                pos = Vector3.zero;
                Tracking.BodyMountCorrection.Apply(LeftWristRole, ref pos, ref rot);
                Check(Mathf.Abs(Quaternion.Dot(rot, Quaternion.identity) - 1f) < 1e-4f &&
                      (pos - new Vector3(0f, -0.09f, 0f)).magnitude < 1e-6f,
                    "level 90mm on identity: rotation untouched, pos slides -0.09 m");

                // ── role mapping: wrist+hand share the side, elbow untouched ──
                Tracking.BodyMountCorrection.SetSide("right", yaw: 30f, level: 0f);
                var rightRot = Quaternion.identity;
                var rightPos = Vector3.one;
                Check(Tracking.BodyMountCorrection.Apply(RightWristRole, ref rightPos, ref rightRot),
                    "right wrist corrected");
                Check(Tracking.BodyMountCorrection.Apply(LeftHandRole, ref pos, ref rot),
                    "left hand shares left params");
                var elbowRot = Quaternion.identity;
                var elbowPos = Vector3.one;
                Check(!Tracking.BodyMountCorrection.Apply(ElbowRole, ref elbowPos, ref elbowRot)
                      && Mathf.Abs(Quaternion.Dot(elbowRot, Quaternion.identity) - 1f) < 1e-6f,
                    "elbow (role 19) untouched");
                Check(Mathf.Abs(Quaternion.Dot(rightRot, Quaternion.Euler(0f, 30f, 0f)) - 1f) < 1e-4f,
                    "right side params independent of left");

                // ── persistence round-trip incl. translation (offline-fit seed) ──
                string seeded =
                    "{\"enabled\":true," +
                    "\"left\":{\"yaw\":15.0,\"level\":-4.0,\"translation\":{\"x\":0.05,\"y\":0.01,\"z\":-0.02}}," +
                    "\"right\":{\"yaw\":-27.0,\"level\":3.0,\"translation\":{\"x\":-0.05,\"y\":0.01,\"z\":-0.02}}}";
                File.WriteAllText(scratch, seeded);
                Tracking.BodyMountCorrection.LoadForTest(scratch);
                var seededSide = Tracking.BodyMountCorrection.GetSide("left");
                Check(seededSide != null && Mathf.Abs(seededSide.yaw - 15f) < 1e-4f
                      && Mathf.Abs(seededSide.level + 4f) < 1e-4f
                      && (seededSide.translation - new Vector3(0.05f, 0.01f, -0.02f)).magnitude < 1e-5f,
                    "seeded config loads (yaw/level/translation)");

                pos = Vector3.zero;
                rot = Quaternion.identity;
                Tracking.BodyMountCorrection.Apply(LeftWristRole, ref pos, ref rot);
                // p' = -q' * ((0, level mm) + t) with q' = Euler(0, yaw, 0):
                // level -4 mm slides up-axis, translation seeds in additively.
                var expectedPos = -(Quaternion.Euler(0f, 15f, 0f) *
                    (new Vector3(0f, -0.004f, 0f) + new Vector3(0.05f, 0.01f, -0.02f)));
                Check((pos - expectedPos).magnitude < 1e-5f, "translation math: p' = p - q' (slide + t)");
                Check(Mathf.Abs(Quaternion.Dot(rot, Quaternion.Euler(0f, 15f, 0f)) - 1f) < 1e-4f,
                    "seeded rotation math: q' = q * Euler(0, yaw, 0)");

                // SetSide persists (file reflects the write)
                Tracking.BodyMountCorrection.SetSide("left", yaw: 16.5f, level: -4f);
                Tracking.BodyMountCorrection.LoadForTest(scratch);
                var persisted = Tracking.BodyMountCorrection.GetSide("left");
                Check(persisted != null && Mathf.Abs(persisted.yaw - 16.5f) < 1e-4f,
                    "SetSide persists across reload");

                // ── receiver-format BridgeControl drives the store ──
                var managerObject = new GameObject("MountSmokeManager");
                try
                {
                    var manager = managerObject.AddComponent<PicoBridgeManager>();
                    // Awake ran EnsureLoaded on persistentDataPath — re-point
                    // the store at the scratch file before driving it.
                    Tracking.BodyMountCorrection.ResetForTest(scratch);
                    const string setCorrection =
                        "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_mount_correction\", " +
                        "\"payload\": {\"enabled\": true, \"left\": {\"yaw\": 15.0, \"level\": -4.0}, " +
                        "\"right\": {\"yaw\": -27.0, \"level\": 3.0}}}";
                    InvokePrivate(manager, "HandleBridgeControl", setCorrection);

                    var left = Tracking.BodyMountCorrection.GetSide("left");
                    var right = Tracking.BodyMountCorrection.GetSide("right");
                    Check(left != null && Mathf.Abs(left.yaw - 15f) < 1e-4f && Mathf.Abs(left.level + 4f) < 1e-4f,
                        "set_mount_correction: left parsed");
                    Check(right != null && Mathf.Abs(right.yaw + 27f) < 1e-4f && Mathf.Abs(right.level - 3f) < 1e-4f,
                        "set_mount_correction: right parsed (negative yaw)");

                    Tracking.BodyMountCorrection.LoadForTest(scratch);
                    var remotePersisted = Tracking.BodyMountCorrection.GetSide("right");
                    Check(remotePersisted != null && Mathf.Abs(remotePersisted.yaw + 27f) < 1e-4f,
                        "remote push persists to local config");

                    // partial payload: absent side keeps its stored values
                    const string setLeftOnly =
                        "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_mount_correction\", " +
                        "\"payload\": {\"enabled\": true, \"left\": {\"yaw\": 20.0, \"level\": 0.0}}}";
                    InvokePrivate(manager, "HandleBridgeControl", setLeftOnly);
                    var keptRight = Tracking.BodyMountCorrection.GetSide("right");
                    Check(keptRight != null && Mathf.Abs(keptRight.yaw + 27f) < 1e-4f,
                        "absent side keeps stored values");

                    const string setDisabled =
                        "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_mount_correction\", " +
                        "\"payload\": {\"enabled\": false}}";
                    InvokePrivate(manager, "HandleBridgeControl", setDisabled);
                    Check(!Tracking.BodyMountCorrection.Enabled, "enabled:false parsed (t10 held-grip path)");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(managerObject);
                }

                // ── editor mock parity: Body joints 20-23 corrected, others not ──
                Tracking.BodyMountCorrection.ResetForTest(scratch);
                Tracking.BodyMountCorrection.SetSide("left", yaw: 45f, level: 0f);
                string mockJson = Tracking.MockTrackingData.GenerateJson(1.234f);
                string bodyBlock = ExtractJsonArray(mockJson, "\"Body\":{\"joints\":[");
                Check(bodyBlock.Length > 0, "mock body block present");
                var joints = bodyBlock.Split(new[] { "},{" }, StringSplitOptions.RemoveEmptyEntries);
                Check(joints.Length >= 24, "mock body has 24 joints");
                Check(QuaternionOf(joints[ElbowRole]).w > 0.999f, "mock elbow stays identity");
                var mockWrist = QuaternionOf(joints[LeftWristRole]);
                Check(Mathf.Abs(Quaternion.Dot(mockWrist, Quaternion.Euler(0f, 45f, 0f)) - 1f) < 1e-3f,
                    "mock left wrist corrected (yaw 45)");
                var mockRight = QuaternionOf(joints[21]);
                Check(mockRight.w > 0.999f, "mock right wrist stays identity (zero params)");
            }
            finally
            {
                if (File.Exists(scratch))
                    File.Delete(scratch);
            }

            Debug.Log($"[MOUNTSMOKE] PASS ({checks} checks)");
        }

        private static Quaternion QuaternionOf(string jointJson)
        {
            int pIndex = jointJson.IndexOf("\"p\":\"", StringComparison.Ordinal);
            string pose = jointJson.Substring(pIndex + 5, jointJson.IndexOf('"', pIndex + 5) - pIndex - 5);
            var parts = pose.Split(',');
            return new Quaternion(
                float.Parse(parts[3], CultureInfo.InvariantCulture),
                float.Parse(parts[4], CultureInfo.InvariantCulture),
                float.Parse(parts[5], CultureInfo.InvariantCulture),
                float.Parse(parts[6], CultureInfo.InvariantCulture));
        }

        private static string ExtractJsonArray(string json, string marker)
        {
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;
            int open = json.IndexOf('[', start);
            int close = json.IndexOf(']', open);
            if (open < 0 || close < 0)
                return string.Empty;
            return json.Substring(open + 1, close - open - 1);
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[MOUNTSMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }
    }
}
#endif
