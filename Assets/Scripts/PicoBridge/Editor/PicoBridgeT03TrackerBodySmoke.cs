#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the tracker-mode body channel (tracker-ik map t03
    /// channel, t09 content): third ArmStreamMode state, BridgeControl
    /// set_trackers entry, mutex round-trips, and the 24-joint IK wire frame
    /// (content assertions live in the T09 smoke; this one pins the block
    /// contract and the head-slot flip ruling). Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT03TrackerBodySmoke.Run -quit \
    ///     -logFile &lt;log&gt;
    ///
    /// Success marker: "[T03SMOKE] PASS". Expected values are hand-written
    /// golden literals (wire contract / flipped head convention), never
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

                // ── 7. tracker-mode wire frame (t09 content: real IK) ──
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
                var json = sb.ToString();
                Check(json.StartsWith(",\"Body\":{\"poseSpace\":\"pico_body_local\",\"alignment\":\"pico_native\",\"joints\":[") &&
                      json.EndsWith("],\"len\":24}"),
                    "wire: body block contract (poseSpace/alignment/24 joints/len)");
                var poses = PoseFields(json);
                Check(poses.Length == 24, "wire: 24 joint poses");
                // t09 ruling: the head source is NATIVE; the wire carries
                // the flipped Unity convention — this golden (negated
                // z/qz/qw) cannot pass a raw passthrough.
                Check(poses[15] == "0.500000,-0.250000,-1.000000,0.100000,0.200000,-0.300000,-0.900000",
                    "wire: head slot = flipped HMD pose (t09 uniform-convention ruling)");
                Check(poses[0] != "0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,1.000000",
                    "wire: body content is IK (pelvis = standing template, not identity)");
                Check(json.Contains("\"t\":1234567"), "wire: head timestamp propagates");

                // Head unavailable (no source): identity head slot, t=0, still len 24.
                TrackerBodyHead.Source = null;
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                json = sb.ToString();
                poses = PoseFields(json);
                Check(poses.Length == 24 && poses[15] == "0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,1.000000",
                    "wire: head source absent → identity head slot (len 24)");
                Check(!json.Contains("\"t\":1234567") && json.Contains("\"t\":0"),
                    "wire: head source absent → t=0");
            }
            finally
            {
                TrackerBodyHead.Source = null;
                UnityEngine.Object.DestroyImmediate(managerObject);
            }

            Debug.Log($"[T03SMOKE] PASS ({checks} checks)");
        }

        /// <summary>All 24 joint "p" pose strings, in role order.</summary>
        private static string[] PoseFields(string bodyJson)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(bodyJson, "\"p\":\"([^\"]*)\"");
            var fields = new string[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                fields[i] = matches[i].Groups[1].Value;
            return fields;
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
