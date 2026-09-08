#if UNITY_EDITOR
using System;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the tracker gizmos' display rules (tracker-ik map
    /// t02): the same rule set as the body avatar (BodyTrackingBlockDriver) —
    /// hidden while stereo immersive FPV owns the view, then restored with
    /// ghost semantics intact. Run headless like T01/T07.
    ///
    /// Success marker: "[T02SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT02VizSmoke
    {
        private static readonly Vector3 Pose = new Vector3(0.5f, -0.25f, -1.0f);
        // Normalized: device quats arrive normalized, and the t07 mapping
        // multiplies vectors by this rotation (Unity does not renormalize).
        private static readonly Quaternion Rot = new Quaternion(0.1f, 0.2f, -0.3f, -0.9f).normalized;

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T02SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T02SMOKE] ok: " + label);
            }

            bool Nearly(float a, float b) => Mathf.Abs(a - b) < 1e-5f;

            MotionTrackerBinding.ResetForTest();
            MotionTrackerBinding.SetBindingStateForTest(true, 7, -1, 7);
            TrackerFrameCache.ResetForTest();
            TrackerFrameCache.Clock = () => 100f;

            var vizObject = new GameObject("T02SmokeViz");
            try
            {
                var viz = vizObject.AddComponent<MotionTrackerVisualizer>();
                InvokePrivate(viz, "Start");
                var leftGizmo = GetPrivate(viz, "_left");
                var leftRootGo = (GameObject)GetPrivate(leftGizmo, "Root");
                var cubeMat = (Material)GetPrivate(leftGizmo, "CubeMaterial");

                // Baseline: fresh valid frame shows the gizmo.
                TrackerFrameCache.PublishValid("left", 7, Pose, Rot, 100f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(leftRootGo.activeSelf, "viz: baseline shows on valid frame");

                // Immersive FPV owns the view → hidden even with fresh data
                // (same rule as the body avatar, BodyTrackingBlockDriver.Update).
                bool immersive = true;
                viz.ImmersiveHideOverride = () => immersive;
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(!leftRootGo.activeSelf, "viz: hidden while immersive FPV active");

                // Immersive ends with the side optically lost in the meantime:
                // gizmo returns as a gray ghost holding the last pose. Zero
                // the ghost grace window for this check (the t10 debounce
                // keeps color through brief validity flickers otherwise).
                immersive = false;
                MotionTrackerVisualizer.GhostGraceS = 0f;
                TrackerFrameCache.PublishInvalid("left", 7, 100.2f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(leftRootGo.activeSelf && Nearly(leftRootGo.transform.position.z, -1.0f) &&
                      Nearly(cubeMat.color.r, 0.4f) && Nearly(cubeMat.color.g, 0.4f) && Nearly(cubeMat.color.b, 0.44f),
                    "viz: after immersive, lost side shows gray ghost at last pose");

                // t10 debounce: a brief validity flicker (like a still hand)
                // stays side-colored through the grace window — recolor on
                // the next valid sample, gray only on sustained loss. The
                // puck gizmo may be muted (the trim mapping is always on),
                // so assert "not ghost gray" rather than an exact color.
                MotionTrackerVisualizer.GhostGraceS = 0.7f;
                TrackerFrameCache.PublishValid("left", 7, Pose, Rot, 100.3f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                TrackerFrameCache.PublishInvalid("left", 7, 100.4f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(!(Nearly(cubeMat.color.r, 0.4f) && Nearly(cubeMat.color.g, 0.4f) && Nearly(cubeMat.color.b, 0.44f)),
                    "viz: brief flicker stays side-colored inside the ghost grace window");
                MotionTrackerVisualizer.GhostGraceS = 0f; // legacy immediate-gray for the t07 ghost checks below

                // Immersive again with no fresh data at all → stays hidden.
                immersive = true;
                TrackerFrameCache.Clock = () => 105f; // everything stale
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(!leftRootGo.activeSelf, "viz: hidden while immersive regardless of data");

                // ── t07: mapped-hand gizmo (calibration verification) ──
                immersive = false;
                TrackerFrameCache.Clock = () => 200f;
                TrackerHandCalibration.ResetForTest(
                    System.IO.Path.Combine(Application.temporaryCachePath, "t02-t07-store.json"));

                // ── t17: mapped-hand gizmo under the human-trim model ──
                // Zero trim (the fresh-store default): there is no
                // "uncalibrated" state anymore — the hand gizmo is always
                // mapped and coincides with the puck; the raw puck dims.
                TrackerFrameCache.PublishValid("left", 7, Pose, Rot, 200f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                var handGizmo = GetPrivate(viz, "_leftHand");
                var handRootGo = (GameObject)GetPrivate(handGizmo, "Root");
                var handCubeMat = (Material)GetPrivate(handGizmo, "CubeMaterial");
                var rotN = Rot.normalized; // Unity normalizes on assignment
                Check(handRootGo.activeSelf &&
                      Nearly(handRootGo.transform.position.x, Pose.x) &&
                      Nearly(handRootGo.transform.position.y, Pose.y) &&
                      Nearly(handRootGo.transform.position.z, Pose.z) &&
                      Nearly(handRootGo.transform.rotation.x, rotN.x) && Nearly(handRootGo.transform.rotation.w, rotN.w),
                    "t17: zero trim maps the hand gizmo onto the puck pose");
                Check(leftRootGo.activeSelf && cubeMat.color.r < 0.6f && cubeMat.color.r > 0.3f,
                    "t17: raw puck gizmo dims under the always-on mapping");
                Check(handCubeMat.color.r > 0.9f, "t17: hand gizmo carries the bright side color");

                // Trimmed: hand gizmo at puck − trimmedRot * (0, level, 0),
                // rotation = puckRot * Euler(yaw,...) — the panel knobs'
                // live effect.
                TrackerHandCalibration.SetSide("left", 90f, 0f, 0f, 20f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                var trimmedRot = rotN * Quaternion.Euler(0f, 90f, 0f);
                var expected = Pose - trimmedRot * new Vector3(0f, 0.020f, 0f);
                Check(handRootGo.activeSelf &&
                      Nearly(handRootGo.transform.position.x, expected.x) &&
                      Nearly(handRootGo.transform.position.y, expected.y) &&
                      Nearly(handRootGo.transform.position.z, expected.z),
                    "t17: trim moves the hand gizmo (yaw + level slide)");

                // Optical loss under calibration: hand gizmo ghosts at the
                // last mapped pose.
                TrackerFrameCache.PublishInvalid("left", 7, 200.2f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(handRootGo.activeSelf && Nearly(handRootGo.transform.position.x, expected.x) &&
                      Nearly(handCubeMat.color.r, 0.4f),
                    "t07: lost side ghosts the hand gizmo at the last mapped pose");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(vizObject);
            }

            MotionTrackerBinding.ResetForTest();
            TrackerFrameCache.ResetForTest();

            Debug.Log($"[T02SMOKE] PASS ({checks} checks)");
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T02SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static object GetPrivate(object target, string field)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T02SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            return info.GetValue(target);
        }
    }
}
#endif
