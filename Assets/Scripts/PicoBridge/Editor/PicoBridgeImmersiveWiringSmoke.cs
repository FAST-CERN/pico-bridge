#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Discriminating repro for the "Start button enters immersive mode but
    /// tex=null forever" device report (2026-09-05): rebuild the manager's
    /// runtime self-assembly in batch mode and assert the immersive
    /// controller actually ends up wired to the teleimager signaling client
    /// (SetImmersive silently skips StartStream when the reference or
    /// IsConfigured is false, so a broken wire shows up as a black quad with
    /// zero [HttpSignaling] logs — exactly the on-device evidence).
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeImmersiveWiringSmoke.Run -logFile &lt;log&gt;
    ///
    /// Success marker: "[IMMWIRE] PASS".
    /// </summary>
    public static class PicoBridgeImmersiveWiringSmoke
    {
        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[IMMWIRE] FAIL: " + label);
                checks++;
                Debug.Log("[IMMWIRE] ok: " + label);
            }

            var managerObject = new GameObject("ImmWireSmokeManager");
            try
            {
                // Edit mode never ticks Awake/Start — drive them manually in
                // the same order the player would (t07-smoke pattern).
                var manager = managerObject.AddComponent<PicoBridgeManager>();
                InvokePrivate(manager, "Awake");

                var bootstrap = UnityEngine.Object.FindObjectOfType<PicoBridge.Immersive.StereoImmersiveBootstrap>();
                Check(bootstrap != null, "manager Awake created the immersive bootstrap");

                var client = manager.TeleimagerStream;
                Check(client != null, "teleimager signaling client exists");
                Check(client.IsConfigured, $"client IsConfigured (url={client.ServerUrl})");

                // Batch mode never ticks Start phases — drive the bootstrap's
                // Start manually (same pattern as the t07 smoke's viz Start).
                InvokePrivate(bootstrap, "Start");

                var controller = UnityEngine.Object.FindObjectOfType<PicoBridge.Immersive.StereoImmersiveController>();
                Check(controller != null, "bootstrap Start built the immersive controller");
                var teleField = GetPrivateField(controller, "teleimagerStream");
                Check(ReferenceEquals(teleField, client), "controller wired to THIS teleimager client");
                var camField = GetPrivateField(controller, "webRtcCamera");
                Check(camField != null, "controller wired to the webrtc camera receiver");

                // The entry call site must actually reach StartStream with
                // this state (mirrors SetImmersive's silent guard).
                bool wouldStart = teleField is PicoBridge.Camera.WebRtcHttpSignalingClient c && c.IsConfigured;
                Check(wouldStart, "SetImmersive guard passes: StartStream would fire on entry");

                // ── exit-gesture edge+sustain (deploy-map strap conflict) ──
                // Edit mode cannot run the connect coroutine, so drive the
                // entry with the teleimager reference cleared (the guard then
                // skips StartStream) and exercise the grip filter directly.
                // The controller's Awake (rig auto-resolve) also never ran in
                // edit mode — invoke it first like the player would.
                InvokePrivate(controller, "Awake");
                SetPrivate(controller, "teleimagerStream", null);
                controller.EnterImmersive();
                Check(controller.IsImmersiveActive, "entry with strap-held grip stays immersive");

                bool exited = false;
                for (float t = 0f; t < 5f; t += 0.1f)
                    exited |= InvokePrivateBool(controller, "ShouldExitOnGrip", true, t);
                Check(!exited, "grip held since entry: no exit within 5 s (strap case)");

                InvokePrivateBool(controller, "ShouldExitOnGrip", false, 5.2f);
                InvokePrivateBool(controller, "ShouldExitOnGrip", true, 5.5f);
                Check(!InvokePrivateBool(controller, "ShouldExitOnGrip", true, 6.29f), "deliberate hold 0.79 s: not yet");
                Check(InvokePrivateBool(controller, "ShouldExitOnGrip", true, 6.31f), "deliberate hold 0.81 s: exits");

                controller.EnterImmersive(); // fresh entry state
                InvokePrivateBool(controller, "ShouldExitOnGrip", false, 7.0f);
                InvokePrivateBool(controller, "ShouldExitOnGrip", true, 7.1f);
                Check(!InvokePrivateBool(controller, "ShouldExitOnGrip", true, 7.39f), "strap flicker 0.29 s: no exit");
                InvokePrivateBool(controller, "ShouldExitOnGrip", false, 7.4f);
                Check(!InvokePrivateBool(controller, "ShouldExitOnGrip", true, 7.5f), "after flicker release: still immersive");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(managerObject);
                var rig = GameObject.Find("StereoImmersiveRig");
                if (rig != null)
                    UnityEngine.Object.DestroyImmediate(rig);
            }

            Debug.Log($"[IMMWIRE] PASS ({checks} checks)");
        }

        private static object GetPrivateField(object target, string field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[IMMWIRE] FAIL: field {target.GetType().Name}.{field} not found");
            return info.GetValue(target);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[IMMWIRE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[IMMWIRE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static bool InvokePrivateBool(object target, string method, params object[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[IMMWIRE] FAIL: method {target.GetType().Name}.{method} not found");
            return (bool)info.Invoke(target, args);
        }
    }
}
#endif
