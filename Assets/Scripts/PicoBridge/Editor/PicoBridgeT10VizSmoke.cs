#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the IK-mode avatar display set (tracker-ik map
    /// t10): upper-chain-only rendering with shrunk HEAD/NECK in TrackerBody,
    /// the bodytrack-deploy t07 contract (HEAD/NECK hidden) in every other
    /// mode, exact restore on switching back, and the chatty-proof status
    /// channel (avatar_status.jsonl) that carries the avatar-visibility
    /// forensics for the device round. Run headless like the T17 smoke;
    /// marker "[T10SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT10VizSmoke
    {
        // BodyTrackerRole enum spellings, index order.
        private static readonly string[] RoleNames =
        {
            "Pelvis", "LEFT_HIP", "RIGHT_HIP", "SPINE1", "LEFT_KNEE", "RIGHT_KNEE",
            "SPINE2", "LEFT_ANKLE", "RIGHT_ANKLE", "SPINE3", "LEFT_FOOT", "RIGHT_FOOT",
            "NECK", "LEFT_COLLAR", "RIGHT_COLLAR", "HEAD", "LEFT_SHOULDER", "RIGHT_SHOULDER",
            "LEFT_ELBOW", "RIGHT_ELBOW", "LEFT_WRIST", "RIGHT_WRIST", "LEFT_HAND", "RIGHT_HAND",
        };

        private const int Neck = 12;
        private const int Head = 15;

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T10SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T10SMOKE] ok: " + label);
            }

            // ── 1. pure display-set table ──────────────────────────
            Check(Mathf.Abs(BodyTrackingBlockDriver.HeadShrinkFactor - 0.4f) < 1e-6f,
                "head shrink factor = 0.4 (grill ruling A)");
            bool bodyOk = true, trackerOk = true, rangeOk = true;
            for (int role = 0; role < 24; role++)
            {
                bool bodyVisible = BodyTrackingBlockDriver.IsRoleVisible(role, false);
                bool trackerVisible = BodyTrackingBlockDriver.IsRoleVisible(role, true);
                // body/idle: t07 contract — only HEAD/NECK hidden
                bodyOk &= bodyVisible == (role != Neck && role != Head);
                // tracker (06:38 ruling): upper chain minus HEAD (NECK only
                // head-line anchor, shrunk)
                trackerOk &= trackerVisible == (role >= 12 && role != Head);
            }
            rangeOk &= !BodyTrackingBlockDriver.IsRoleVisible(-1, true);
            rangeOk &= !BodyTrackingBlockDriver.IsRoleVisible(24, true);
            rangeOk &= !BodyTrackingBlockDriver.IsRoleVisible(99, false);
            Check(bodyOk, "body/idle set: HEAD/NECK hidden, rest visible (t07 zero-regression)");
            Check(trackerOk, "tracker set: lower body + HEAD hidden, upper chain + NECK visible (t10 06:38)");
            Check(rangeOk, "out-of-range roles refused");

            // ── 2. synthetic hierarchy application ─────────────────
            var root = new GameObject("T10SkeletonRoot");
            var host = new GameObject("T10DriverHost");
            try
            {
                var baseScales = new Vector3[24];
                for (int role = 0; role < 24; role++)
                {
                    var joint = new GameObject(RoleNames[role]);
                    joint.transform.SetParent(root.transform, false);
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = "Cube";
                    cube.transform.SetParent(joint.transform, false);
                    // Distinct base scale per role: a restore bug cannot
                    // hide behind a uniform scale.
                    baseScales[role] = new Vector3(0.02f + role * 0.001f, 0.02f, 0.02f);
                    cube.transform.localScale = baseScales[role];
                }

                var driver = host.AddComponent<BodyTrackingBlockDriver>();
                SetPrivate(driver, "_root", root.transform);
                InvokePrivate(driver, "MapJointsByName");

                void Apply(bool trackerBody)
                {
                    BodyTrackingBlockDriver.SetDisplayMode(trackerBody);
                    InvokePrivate(driver, "ApplyDisplaySet");
                }

                Transform CubeOf(int role) => root.transform.Find(RoleNames[role] + "/Cube");

                Apply(false);
                bool bodyApplied = true;
                for (int role = 0; role < 24; role++)
                {
                    var cube = CubeOf(role);
                    bodyApplied &= cube.gameObject.activeSelf == (role != Neck && role != Head);
                    bodyApplied &= (cube.localScale - baseScales[role]).magnitude < 1e-6f;
                }
                Check(bodyApplied, "apply body set on synthetic hierarchy: visibility + scales exact");

                Apply(true);
                bool trackerApplied = true;
                for (int role = 0; role < 24; role++)
                {
                    var cube = CubeOf(role);
                    trackerApplied &= cube.gameObject.activeSelf == (role >= 12 && role != Head);
                    if (role == Neck)
                        trackerApplied &= (cube.localScale - baseScales[role] * 0.4f).magnitude < 1e-6f;
                    else
                        trackerApplied &= (cube.localScale - baseScales[role]).magnitude < 1e-6f;
                }
                Check(trackerApplied, "apply tracker set: lower + HEAD hidden, upper + NECK visible, NECK shrunk ×0.4");

                Apply(false);
                bool restored = true;
                for (int role = 0; role < 24; role++)
                {
                    var cube = CubeOf(role);
                    restored &= cube.gameObject.activeSelf == (role != Neck && role != Head);
                    restored &= (cube.localScale - baseScales[role]).magnitude < 1e-6f;
                }
                Check(restored, "switch back to body set restores visibility and scales exactly");

                Apply(true);
                InvokePrivate(driver, "ApplyDisplaySet"); // idempotent re-apply
                var neckCube = CubeOf(Neck);
                var headCubeAfter = CubeOf(Head);
                Check(neckCube.gameObject.activeSelf &&
                      (neckCube.localScale - baseScales[Neck] * 0.4f).magnitude < 1e-6f &&
                      !headCubeAfter.gameObject.activeSelf,
                    "re-apply is idempotent (no scale compounding, HEAD stays hidden)");

                // ── 3. status channel (chatty-proof forensics) ─────
                InvokePrivate(driver, "AppendStatus", "smoke");
                var statusPath = Path.Combine(Application.persistentDataPath, "avatar_status.jsonl");
                Check(File.Exists(statusPath), "avatar_status.jsonl channel exists");
                // Flat object of scalars — a substring contract is enough
                // (JsonUtility wants a wrapper type for this shape).
                var lastLine = File.ReadAllLines(statusPath).Last(ln => ln.Trim().Length > 0);
                Check(lastLine.Contains("\"kind\":\"smoke\"") &&
                      lastLine.Contains("\"mode\":\"tracker\"") &&
                      lastLine.Contains("\"mapped\":24") &&
                      lastLine.Contains("\"cubes\":24") &&
                      lastLine.Contains("\"rootActive\":true"),
                    "status line carries kind/mode/mapped/cubes/rootActive");

                // ── 4. tracker-mode body signal seam (t10 root-cause fix) ──
                // The avatar's TrackingVisualSignalGate reads Body validity;
                // with SDK body tracking stopped by the mode mutex, the
                // tracker-mode frames are the signal that opens the gate.
                Check(!TrackingSignalStatus.HasRecentTrackerBodyFrames(),
                    "no tracker body frames noted → body signal not fresh (gate stays closed)");
                TrackingSignalStatus.NoteTrackerBodyFrame();
                Check(TrackingSignalStatus.HasRecentTrackerBodyFrames(),
                    "noted frame → body signal fresh (avatar gate opens in tracker mode)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(host);
            }

            Debug.Log($"[T10SMOKE] PASS ({checks} checks)");
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T10SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T10SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }
    }
}
#endif
