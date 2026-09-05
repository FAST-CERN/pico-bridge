#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the in-headset calibration steppers (bodytrack-
    /// deploy t08): builds the code-built rows on the real panel prefab,
    /// drives the stepper buttons, and asserts clicks land in the t07
    /// correction store (apply + persist). Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeMountCalibSmoke.Run -logFile &lt;log&gt;
    ///
    /// Success marker: "[CALIBSMOKE] PASS".
    /// </summary>
    public static class PicoBridgeMountCalibSmoke
    {
        private const string PanelPrefabPath = "Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab";

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[CALIBSMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[CALIBSMOKE] ok: " + label);
            }

            string scratch = Path.Combine(Path.GetTempPath(), "pico_bridge_calib_smoke.json");
            if (File.Exists(scratch))
                File.Delete(scratch);
            Tracking.BodyMountCorrection.ResetForTest(scratch);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath);
            Check(prefab != null, "panel prefab loads");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var managerObject = new GameObject("CalibSmokeManager");
            try
            {
                var controller = instance.GetComponentInChildren<UI.PicoBridgePanelController>(true);
                Check(controller != null, "panel controller found on prefab");
                var manager = managerObject.AddComponent<PicoBridgeManager>();
                SetPrivate(controller, "manager", manager);

                InvokePrivate(controller, "ConfigureArmSourceControl");
                InvokePrivate(controller, "ConfigureMountCalibControls");

                var rowL = FindDeep(instance.transform, "MountCalibL");
                var rowR = FindDeep(instance.transform, "MountCalibR");
                Check(rowL != null && rowR != null, "L and R stepper rows built under panel");
                Check(rowL.Find("YawValue") != null && rowL.Find("LevelValue") != null,
                    "value texts built (L)");
                Check(rowR.Find("YawValue") != null && rowR.Find("LevelValue") != null,
                    "value texts built (R)");

                // Stepper buttons in construction order: yaw-, yaw+, lev-, lev+.
                var buttonsL = rowL.GetComponentsInChildren<Button>();
                var buttonsR = rowR.GetComponentsInChildren<Button>();
                Check(buttonsL.Length == 4 && buttonsR.Length == 4, "four steppers per side");

                buttonsL[1].onClick.Invoke(); // L yaw +
                var left = Tracking.BodyMountCorrection.GetSide("left");
                Check(Mathf.Abs(left.yaw - 5f) < 1e-4f && Mathf.Abs(left.level) < 1e-4f,
                    "L yaw + steps yaw to 5 deg (level untouched)");

                buttonsR[2].onClick.Invoke(); // R lev -
                var right = Tracking.BodyMountCorrection.GetSide("right");
                Check(Mathf.Abs(right.level + 5f) < 1e-4f && Mathf.Abs(right.yaw) < 1e-4f,
                    "R lev - steps level to -5 deg (yaw untouched)");

                var yawText = rowL.Find("YawValue").GetComponent<TMPro.TMP_Text>();
                Check(yawText.text.Contains("5"), $"value text refreshed ({yawText.text})");

                Tracking.BodyMountCorrection.LoadForTest(scratch);
                left = Tracking.BodyMountCorrection.GetSide("left");
                right = Tracking.BodyMountCorrection.GetSide("right");
                Check(Mathf.Abs(left.yaw - 5f) < 1e-4f && Mathf.Abs(right.level + 5f) < 1e-4f,
                    "clicks persisted to the device-local config");

                // clamping at ±90 deg
                Tracking.BodyMountCorrection.SetSide("left", 88f, 0f);
                buttonsL[1].onClick.Invoke();
                left = Tracking.BodyMountCorrection.GetSide("left");
                Check(Mathf.Abs(left.yaw - 90f) < 1e-4f, "yaw clamps at +90 deg");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(managerObject);
                if (File.Exists(scratch))
                    File.Delete(scratch);
            }

            Debug.Log($"[CALIBSMOKE] PASS ({checks} checks)");
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
                throw new InvalidOperationException($"[CALIBSMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[CALIBSMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }
    }
}
#endif
