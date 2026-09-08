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
        private static readonly Quaternion Rot = new Quaternion(0.1f, 0.2f, -0.3f, -0.9f);

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
                // gizmo returns as a gray ghost holding the last pose.
                immersive = false;
                TrackerFrameCache.PublishInvalid("left", 7, 100.2f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(leftRootGo.activeSelf && Nearly(leftRootGo.transform.position.z, -1.0f) &&
                      Nearly(cubeMat.color.r, 0.4f) && Nearly(cubeMat.color.g, 0.4f) && Nearly(cubeMat.color.b, 0.44f),
                    "viz: after immersive, lost side shows gray ghost at last pose");

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

                // Uncalibrated: full-color puck, hand gizmo hidden.
                TrackerFrameCache.PublishValid("left", 7, Pose, Rot, 200f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                var handGizmo = GetPrivate(viz, "_leftHand");
                var handRootGo = (GameObject)GetPrivate(handGizmo, "Root");
                var handCubeMat = (Material)GetPrivate(handGizmo, "CubeMaterial");
                Check(!handRootGo.activeSelf && Nearly(cubeMat.color.r, 1.0f),
                    "t07: uncalibrated side keeps full-color puck, hand gizmo hidden");

                // Identity-R + known-t calibration: hand gizmo at pose+t with
                // the same rotation; the raw puck dims.
                var map = new TrackerHandCalibration.SideParams
                {
                    qx = 0f, qy = 0f, qz = 0f, qw = 1f,
                    tx = 0.1f, ty = 0.05f, tz = -0.2f,
                    positionRms = 0.01f, rotationRmsDeg = 2f,
                    poseSet = "chest/side/front",
                };
                TrackerHandCalibration.Commit("left", map);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                var rotN = Rot.normalized; // Unity normalizes on assignment
                Check(handRootGo.activeSelf &&
                      Nearly(handRootGo.transform.position.x, Pose.x + 0.1f) &&
                      Nearly(handRootGo.transform.position.y, Pose.y + 0.05f) &&
                      Nearly(handRootGo.transform.position.z, Pose.z - 0.2f) &&
                      Nearly(handRootGo.transform.rotation.x, rotN.x) && Nearly(handRootGo.transform.rotation.w, rotN.w),
                    "t07: calibrated side renders hand gizmo at puck pose + t (identity R)");
                Check(leftRootGo.activeSelf && cubeMat.color.r < 0.6f && cubeMat.color.r > 0.3f,
                    "t07: raw puck gizmo dims under an active calibration");
                Check(handCubeMat.color.r > 0.9f, "t07: hand gizmo carries the bright side color");

                // Optical loss under calibration: hand gizmo ghosts at the
                // last mapped pose.
                TrackerFrameCache.PublishInvalid("left", 7, 200.2f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(handRootGo.activeSelf && Nearly(handRootGo.transform.position.x, Pose.x + 0.1f) &&
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
