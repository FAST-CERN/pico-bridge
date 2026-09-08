#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UI;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the panel tracker toggle + UX batch (tracker-ik
    /// map t04): third Trackers pill (three-state mutex, routes to
    /// TrackerBody), mount-calib rows hidden outside Body mode, dual LED
    /// status dots (four-color tracker state machine), and gizmo L/R
    /// TextMesh labels. Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT04PanelTrackerSmoke.Run -quit \
    ///     -logFile &lt;log&gt;
    ///
    /// Success marker: "[T04SMOKE] PASS". LED colors are hand-written golden
    /// literals (2026-09-08 grilling: green=valid, yellow=optical ghost,
    /// red=disconnected, gray=unbound).
    /// </summary>
    public static class PicoBridgeT04PanelTrackerSmoke
    {
        private static readonly Color LedValid = new Color(0.16f, 0.74f, 0.43f, 1f);
        private static readonly Color LedLost = new Color(0.96f, 0.63f, 0.16f, 1f);
        private static readonly Color LedDisconnected = new Color(0.88f, 0.22f, 0.29f, 1f);
        private static readonly Color LedUnbound = new Color(0.45f, 0.49f, 0.52f, 1f);
        private static readonly Color LeftSide = new Color(1.0f, 0.55f, 0.15f);
        private static readonly Color RightSide = new Color(0.15f, 0.8f, 0.55f);

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T04SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T04SMOKE] ok: " + label);
            }

            bool Nearly(float a, float b) => Mathf.Abs(a - b) < 1e-4f;
            bool ColorIs(Image image, Color golden) =>
                image != null && Nearly(image.color.r, golden.r) &&
                Nearly(image.color.g, golden.g) && Nearly(image.color.b, golden.b);

            MotionTrackerBinding.ResetForTest();
            TrackerFrameCache.ResetForTest();
            TrackerSessionStatus.ResetForTest();
            TrackerSessionStatus.GuidanceCaller = null;
            TrackerFrameCache.Clock = () => 100f;

            const string PanelPrefabPath = "Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab";
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath);
            Check(prefab != null, "panel prefab loads");
            var instance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            var managerObject = new GameObject("T04SmokeManager");
            try
            {
                var controller = instance.GetComponentInChildren<UI.PicoBridgePanelController>(true);
                Check(controller != null, "panel controller found on prefab");
                var manager = managerObject.AddComponent<PicoBridgeManager>();
                SetPrivate(controller, "manager", manager);
                InvokePrivate(controller, "ConfigureArmSourceControl");
                InvokePrivate(controller, "ConfigureMountCalibControls");

                // ── 1. row build: third pill + LED dots + SN text ──
                var row = FindDeep(instance.transform, "ArmSourceControl");
                Check(row != null, "ArmSourceControl row built under panel");
                Check(row.Find("TrackersButton") != null, "Trackers pill built (t04 restores it, routes to TrackerBody)");
                Check(row.Find("GlovesButton") != null, "Gloves pill built");
                Check(row.Find("HeldButton") != null, "Held pill built");
                var ledLeft = row.Find("LedLeft");
                var ledRight = row.Find("LedRight");
                Check(ledLeft != null && ledLeft.GetComponent<Image>() != null, "left LED dot built (Image)");
                Check(ledRight != null && ledRight.GetComponent<Image>() != null, "right LED dot built (Image)");
                var snText = row.Find("TrackerBinding");
                Check(snText != null, "SN binding text built");
                Check(ledLeft.GetSiblingIndex() < snText.GetSiblingIndex() &&
                      ledRight.GetSiblingIndex() < snText.GetSiblingIndex(),
                    "LED dots sit left of the SN text");

                var trackersPill = row.Find("TrackersButton").GetComponent<Image>();
                var glovesPill = row.Find("GlovesButton").GetComponent<Image>();
                var heldPill = row.Find("HeldButton").GetComponent<Image>();
                var ledLeftImage = ledLeft.GetComponent<Image>();
                var ledRightImage = ledRight.GetComponent<Image>();

                // ── 2. default state: idle mode, all pills dark, LEDs gray ──
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(trackersPill.color.g < 0.3f && glovesPill.color.g < 0.3f && heldPill.color.g < 0.3f,
                    "default: no pill lit (Trackers idle, deploy t10 semantics kept)");
                Check(ColorIs(ledLeftImage, LedUnbound) && ColorIs(ledRightImage, LedUnbound),
                    "default: both LEDs gray (unbound)");

                // ── 3. click routing (pill closures) ──
                row.Find("TrackersButton").GetComponent<Button>().onClick.Invoke();
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody &&
                      manager.sendBody && !manager.sendMotion,
                    "Trackers pill click enters TrackerBody");
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(trackersPill.color.g > 0.3f && glovesPill.color.g < 0.3f && heldPill.color.g < 0.3f,
                    "TrackerBody: only Trackers pill lit");

                row.Find("GlovesButton").GetComponent<Button>().onClick.Invoke();
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Body &&
                      Tracking.BodyMountCorrection.Enabled,
                    "Gloves pill click enters Body with correction on");
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(glovesPill.color.g > 0.3f && trackersPill.color.g < 0.3f && heldPill.color.g < 0.3f,
                    "Body+correction: only Gloves pill lit");

                row.Find("HeldButton").GetComponent<Button>().onClick.Invoke();
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.Body &&
                      !Tracking.BodyMountCorrection.Enabled,
                    "Held pill click keeps Body with correction off");
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(heldPill.color.g > 0.3f && glovesPill.color.g < 0.3f,
                    "Body without correction: only Held pill lit");

                // ── 4. remote-driven mode + mount-calib row visibility ──
                var calibL = FindDeep(instance.transform, "MountCalibL");
                var calibR = FindDeep(instance.transform, "MountCalibR");
                Check(calibL != null && calibR != null, "mount-calib rows built");
                // §3 left the mode at Body (Held): the rows must be visible.
                Check(calibL.gameObject.activeSelf && calibR.gameObject.activeSelf,
                    "Body mode: knob rows visible");

                const string setTrackersOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_trackers\", " +
                    "\"payload\": {\"enabled\": true}}";
                InvokePrivate(manager, "HandleBridgeControl", setTrackersOn);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(manager.ArmStream == PicoBridgeManager.ArmStreamMode.TrackerBody &&
                      !calibL.gameObject.activeSelf && !calibR.gameObject.activeSelf,
                    "set_trackers(true): knob rows hidden (tracker mode)");

                const string setBodyOn =
                    "{\"version\": 1, \"channel\": \"tracking\", \"type\": \"set_body\", " +
                    "\"payload\": {\"enabled\": true, \"height\": 1.82}}";
                InvokePrivate(manager, "HandleBridgeControl", setBodyOn);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(calibL.gameObject.activeSelf && calibR.gameObject.activeSelf,
                    "set_body(true): knob rows visible (Body mode)");

                InvokePrivate(manager, "HandleBridgeControl", setTrackersOn);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(!calibL.gameObject.activeSelf && !calibR.gameObject.activeSelf,
                    "back to tracker mode: knob rows hidden again");

                // ── 5. LED four-color state machine ──
                // Disconnected: bound, powered off.
                MotionTrackerBinding.SetBindingStateForTest(true, 7, 8);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(ColorIs(ledLeftImage, LedDisconnected) && ColorIs(ledRightImage, LedDisconnected),
                    "LED red: bound but disconnected");

                // OpticalLost (left, ghost) vs Disconnected (right).
                MotionTrackerBinding.SetBindingStateForTest(true, 7, 8, 7);
                TrackerFrameCache.PublishInvalid("left", 7, 100f);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(ColorIs(ledLeftImage, LedLost) && ColorIs(ledRightImage, LedDisconnected),
                    "LED yellow: connected without valid pose (left); right still red");

                // Valid (left green), right unchanged red.
                TrackerFrameCache.PublishValid("left", 7, new Vector3(0.5f, -0.25f, -1.0f),
                    new Quaternion(0.1f, 0.2f, -0.3f, -0.9f), 100.1f);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(ColorIs(ledLeftImage, LedValid) && ColorIs(ledRightImage, LedDisconnected),
                    "LED green: fresh valid pose (left)");

                // Unbound again after reset.
                MotionTrackerBinding.ResetForTest();
                TrackerFrameCache.ResetForTest();
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(ColorIs(ledLeftImage, LedUnbound) && ColorIs(ledRightImage, LedUnbound),
                    "LED gray: unbound after reset");

                // ── 6. gizmo L/R TextMesh labels ──
                var vizObject = new GameObject("T04SmokeViz");
                try
                {
                    var viz = vizObject.AddComponent<Tracking.MotionTrackerVisualizer>();
                    InvokePrivate(viz, "Start");
                    var leftRoot = vizObject.transform.Find("TrackerGizmoLeft");
                    var rightRoot = vizObject.transform.Find("TrackerGizmoRight");
                    var leftLabel = leftRoot.Find("sideLabel");
                    var rightLabel = rightRoot.Find("sideLabel");
                    Check(leftLabel != null && rightLabel != null, "gizmo side labels built");
                    var leftMesh = leftLabel.GetComponent<TextMesh>();
                    var rightMesh = rightLabel.GetComponent<TextMesh>();
                    Check(leftMesh != null && leftMesh.text == "L", "left gizmo label reads L");
                    Check(rightMesh != null && rightMesh.text == "R", "right gizmo label reads R");
                    // t04 feedback: the letter sits ON the cube's ±Z faces
                    // (not floating above, where it reads as part of the axis
                    // gizmo), with a mirrored back copy for rear readability.
                    Check(Mathf.Abs(leftLabel.localPosition.y) < 0.005f &&
                          leftLabel.localPosition.z >= 0.014f,
                        "left label on the cube +Z face, not above the axis");
                    Check(Mathf.Abs(rightLabel.localPosition.y) < 0.005f &&
                          rightLabel.localPosition.z >= 0.014f,
                        "right label on the cube +Z face");
                    var leftBack = leftRoot.Find("sideLabelBack");
                    var rightBack = rightRoot.Find("sideLabelBack");
                    Check(leftBack != null && leftBack.GetComponent<TextMesh>().text == "L" &&
                          Mathf.Abs(Mathf.DeltaAngle(leftBack.localEulerAngles.y, 180f)) < 1f,
                        "left back-face label mirrored 180° reads L");
                    Check(rightBack != null && rightBack.GetComponent<TextMesh>().text == "R" &&
                          Mathf.Abs(Mathf.DeltaAngle(rightBack.localEulerAngles.y, 180f)) < 1f,
                        "right back-face label mirrored 180° reads R");
                    // TextMesh stores color 8-bit (0.55 -> 140/255), so the
                    // golden comparison is quantized — that IS the rendered
                    // color contract.
                    bool ColorIs8Bit(TextMesh mesh, Color golden)
                    {
                        var got = (Color32)mesh.color;
                        var want = (Color32)golden;
                        return got.r == want.r && got.g == want.g && got.b == want.b;
                    }
                    Check(ColorIs8Bit(leftMesh, LeftSide),
                        "left label dyed with the left side color (8-bit)");
                    Check(ColorIs8Bit(rightMesh, RightSide),
                        "right label dyed with the right side color (8-bit)");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(vizObject);
                }
            }
            finally
            {
                TrackerSessionStatus.GuidanceCaller = null;
                MotionTrackerBinding.ResetForTest();
                TrackerFrameCache.ResetForTest();
                TrackerFrameCache.Clock = null;
                TrackerSessionStatus.ResetForTest();
                UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }

            Debug.Log($"[T04SMOKE] PASS ({checks} checks)");
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
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T04SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T04SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }
    }
}
#endif
