#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the arm-source mutex (mocap map t07): builds the
    /// panel pill row on the real prefab and drives the real manager with
    /// receiver-format BridgeControl JSON. Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT07Smoke.Run -logFile &lt;log&gt;
    ///
    /// Success marker in the log: "[T07SMOKE] PASS". Any failed check throws,
    /// which surfaces as an executeMethod exception in the log.
    /// </summary>
    public static class PicoBridgeT07Smoke
    {
        private const string PanelPrefabPath = "Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab";

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T07SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T07SMOKE] ok: " + label);
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath);
            Check(prefab != null, "panel prefab loads");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var managerObject = new GameObject("T07SmokeManager");
            try
            {
                // ── panel: build the code-built arm-source row for real ──
                var controller = instance.GetComponentInChildren<UI.PicoBridgePanelController>(true);
                Check(controller != null, "panel controller found on prefab");
                var view = instance.GetComponentInChildren<UI.PicoBridgePanelView>(true);
                Check(view != null && view.resolution720Button != null, "resolution button wired (row template)");

                InvokePrivate(controller, "ConfigureArmSourceControl");
                var row = FindDeep(instance.transform, "ArmSourceControl");
                Check(row != null, "ArmSourceControl row built under panel");
                Check(row.Find("GlovesButton") != null, "Gloves pill built (deploy t10 rework)");
                Check(row.Find("HeldButton") != null, "Held pill built");
                Check(row.Find("TrackersButton") != null, "Trackers pill restored (t04: routes to TrackerBody; legacy set_motion still works)");
                Check(row.Find("TrackerBinding") != null, "SN binding text built");

                // ── manager: receiver-format BridgeControl drives the mutex ──
                var manager = managerObject.AddComponent<PicoBridgeManager>();
                Check(!manager.sendBody && !manager.sendMotion, "default: both streams off (cb46907 contract)");

                SetPrivate(controller, "manager", manager);
                InvokePrivate(controller, "RefreshArmSourceControl");
                var glovesPill = row.Find("GlovesButton").GetComponent<Image>();
                var heldPill = row.Find("HeldButton").GetComponent<Image>();
                Check(glovesPill.color.g < 0.3f && heldPill.color.g < 0.3f,
                    "default: no pill lit (both streams off, deploy t10 semantics)");

                // Python json.dumps output (receiver wire format) — spacing included.
                const string setBodyOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_body\", " +
                    "\"payload\": {\"enabled\": true, \"height\": 1.82}}";
                InvokePrivate(manager, "HandleBridgeControl", setBodyOn);
                Check(manager.sendBody && !manager.sendMotion, "set_body(true): body on, motion off");
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Body, "set_body(true): mode = Body");
                Check(Mathf.Abs(manager.OperatorHeight - 1.82f) < 0.001f, "height 1.82 parsed from payload");

                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(glovesPill.color.g > 0.3f && heldPill.color.g < 0.3f,
                    "panel refresh lights Gloves pill after set_body (correction default on)");

                const string setMotionOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_motion\", " +
                    "\"payload\": {\"enabled\": true}}";
                InvokePrivate(manager, "HandleBridgeControl", setMotionOn);
                Check(!manager.sendBody && manager.sendMotion, "set_motion(true): leaves Body, motion on");
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Trackers, "set_motion(true): mode = Trackers");

                const string setBodyOff =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_body\", " +
                    "\"payload\": {\"enabled\": false}}";
                InvokePrivate(manager, "HandleBridgeControl", setBodyOn); // body on again
                InvokePrivate(manager, "HandleBridgeControl", setBodyOff);
                Check(!manager.sendBody, "set_body(false): body stream off");
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Trackers, "set_body(false): falls back to Trackers mode");

                // ── tracker gizmos (t07 addendum: calibration aid) ──
                var vizObject = new GameObject("T07SmokeViz");
                try
                {
                    var viz = vizObject.AddComponent<Tracking.MotionTrackerVisualizer>();
                    InvokePrivate(viz, "Start");
                    var leftRoot = vizObject.transform.Find("TrackerGizmoLeft");
                    var rightRoot = vizObject.transform.Find("TrackerGizmoRight");
                    Check(leftRoot != null && rightRoot != null, "tracker gizmos built per side");
                    Check(leftRoot.Find("axis0") != null && leftRoot.Find("axis1") != null && leftRoot.Find("axis2") != null,
                        "three axis bars built");
                    Check(leftRoot.Find("tip0") != null && leftRoot.Find("tip1") != null && leftRoot.Find("tip2") != null,
                        "axis direction tips built");
                    Check(!leftRoot.gameObject.activeSelf, "gizmo hidden until side connects");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(vizObject);
                }

                Tracking.MotionTrackerBinding.SetOpticalSample("left", false);
                var describe = Tracking.MotionTrackerBinding.DescribeSides();
                Check(describe == "L:-- R:--", "DescribeSides with no binding: " + describe);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }

            Debug.Log($"[T07SMOKE] PASS ({checks} checks)");
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name)
                    return child;
                var found = FindDeep(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T07SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T07SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }
    }
}
#endif
