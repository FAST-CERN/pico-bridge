#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the tracker-mode body channel (tracker-ik map t03):
    /// third ArmStreamMode state, BridgeControl set_trackers entry, mutex
    /// round-trips, and the transitional 24-joint wire frame. Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT03TrackerBodySmoke.Run -quit \
    ///     -logFile &lt;log&gt;
    ///
    /// Success marker: "[T03SMOKE] PASS". Expected values are hand-written
    /// golden literals (wire contract / no-flip head convention), never
    /// recomputed.
    /// </summary>
    public static class PicoBridgeT03TrackerBodySmoke
    {
        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T03SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T03SMOKE] ok: " + label);
            }

            // Hand-written goldens (receiver 0.2.x Body block contract). The
            // head pose is chosen with positive z/qz/qw so a false flip
            // (-Z, -Qz, -Qw — the AppendBody convention) cannot pass.
            const string IdentityJoint =
                "{\"p\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,1.000000\",\"t\":1234567," +
                "\"va\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"," +
                "\"wva\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"}";
            const string HeadJoint =
                "{\"p\":\"0.500000,-0.250000,1.000000,0.100000,0.200000,0.300000,0.900000\",\"t\":1234567," +
                "\"va\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"," +
                "\"wva\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"}";
            string FallbackFrame =
                ",\"Body\":{\"poseSpace\":\"pico_body_local\",\"alignment\":\"pico_native\",\"joints\":[" +
                string.Join(",", Enumerable.Repeat(
                    "{\"p\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,1.000000\",\"t\":0," +
                    "\"va\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"," +
                    "\"wva\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000\"}", 24)) +
                "],\"len\":24}";

            var managerObject = new GameObject("T03SmokeManager");
            try
            {
                var manager = managerObject.AddComponent<PicoBridgeManager>();

                // ── 1. default contract guard (pre-t03 behavior) ──
                Check(!manager.sendBody && !manager.sendMotion,
                    "default: both streams off (cb46907 contract)");
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Trackers,
                    "default: mode Trackers idle");

                // ── 2. set_trackers enters the third state ──
                const string setTrackersOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_trackers\", " +
                    "\"payload\": {\"enabled\": true}}";
                InvokePrivate(manager, "HandleBridgeControl", setTrackersOn);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody,
                    "set_trackers(true): mode = TrackerBody");
                Check(manager.sendBody && !manager.sendMotion,
                    "set_trackers(true): sendBody on, sendMotion off");
                Check(!BodyTrackingRuntime.IsStarted,
                    "TrackerBody does not start SDK body tracking");

                // ── 3. default receiver handshake must not knock out ──
                // pc-receive with default flags (--arm-source tracker, no
                // --motion-trackers) pushes set_motion(false) on connect; the
                // D2 matrix re-enters the TrackerBody case and keeps sendBody.
                const string setMotionOff =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_motion\", " +
                    "\"payload\": {\"enabled\": false}}";
                InvokePrivate(manager, "HandleBridgeControl", setMotionOff);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody && manager.sendBody,
                    "handshake set_motion(false) keeps TrackerBody (matrix re-entry)");

                // ── 4. explicit receiver authority still switches ──
                const string setMotionOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_motion\", " +
                    "\"payload\": {\"enabled\": true}}";
                InvokePrivate(manager, "HandleBridgeControl", setMotionOn);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Trackers &&
                      manager.sendMotion && !manager.sendBody,
                    "set_motion(true) leaves TrackerBody (receiver authority)");

                InvokePrivate(manager, "HandleBridgeControl", setTrackersOn);
                const string setBodyOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_body\", " +
                    "\"payload\": {\"enabled\": true, \"height\": 1.82}}";
                InvokePrivate(manager, "HandleBridgeControl", setBodyOn);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Body && manager.sendBody,
                    "set_body(true) leaves TrackerBody into Body");

                InvokePrivate(manager, "HandleBridgeControl", setTrackersOn);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody &&
                      !BodyTrackingRuntime.IsStarted,
                    "set_trackers(true) from Body stops SDK body tracking");

                // ── 5. set_trackers(false) -> Trackers idle ──
                const string setTrackersOff =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_trackers\", " +
                    "\"payload\": {\"enabled\": false}}";
                InvokePrivate(manager, "HandleBridgeControl", setTrackersOff);
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Trackers &&
                      !manager.sendBody && !manager.sendMotion,
                    "set_trackers(false): back to Trackers idle (both streams off)");

                // ── 6. public request API (t04 pill routes here) ──
                manager.RequestTrackerBodyMode();
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody &&
                      manager.sendBody && !manager.sendMotion,
                    "RequestTrackerBodyMode enters TrackerBody");

                // ── 7. transitional wire frame golden literal ──
                var collector = new PicoTrackingCollector();
                Check(!collector.TrackerBodyEnabled, "collector: TrackerBodyEnabled default false");
                var sb = (System.Text.StringBuilder)GetPrivate(collector, "_sb");

                TrackerBodyHead.Source = (out Vector3 p, out Quaternion q, out long t) =>
                {
                    p = new Vector3(0.5f, -0.25f, 1.0f);
                    q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
                    t = 1234567;
                    return true;
                };
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                var expected =
                    ",\"Body\":{\"poseSpace\":\"pico_body_local\",\"alignment\":\"pico_native\",\"joints\":[" +
                    string.Join(",", Enumerable.Repeat(IdentityJoint, 15)) + "," + HeadJoint + "," +
                    string.Join(",", Enumerable.Repeat(IdentityJoint, 8)) + "],\"len\":24}";
                Check(sb.ToString() == expected,
                    "wire: transitional body frame golden (15 identity + raw HMD head at idx 15 + 8 identity, len 24)");

                // Head unavailable (no source): full identity frame, t=0, still len 24.
                TrackerBodyHead.Source = null;
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                Check(sb.ToString() == FallbackFrame,
                    "wire: head source absent falls back to full identity frame (len 24, t 0)");
            }
            finally
            {
                TrackerBodyHead.Source = null;
                UnityEngine.Object.DestroyImmediate(managerObject);
            }

            Debug.Log($"[T03SMOKE] PASS ({checks} checks)");
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T03SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static object GetPrivate(object target, string field)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T03SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            return info.GetValue(target);
        }
    }
}
#endif
