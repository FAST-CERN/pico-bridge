using System;
using System.IO;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Strapped-controller mount correction applied to body-tracking output
    /// (bodytrack-deploy t07). With controllers strapped to the hand backs the
    /// PICO solver's held-grip assumption leaves a constant transform error on
    /// the Wrist/Hand joints, and the SDK has no per-joint calibration entry
    /// (map research: post-output correction is the only correction layer).
    /// Moving it here (from the Teleopit provider) means the in-headset
    /// visualisation shows the corrected result the robot will receive.
    ///
    /// Model — mirrors Teleopit's _apply_body_mount_correction exactly so
    /// fitted constants transfer 1:1 (same post-flip joint frame):
    ///
    ///   q' = q * Quaternion.Euler(level, yaw, 0)   (right-multiplied, joint-local)
    ///   p' = p - q' * t                             (t constant per side)
    ///
    /// yaw/level are degrees (operator-tunable, t08 panel knobs + BridgeControl
    /// set_mount_correction); the translation stays out of the UI and is seeded
    /// into the local config by the offline fit (Teleopit
    /// scripts/run/calibrate_body_mount.py). Every write persists to
    /// persistentDataPath/body_mount_correction.json so values survive
    /// restarts as the boot default. The Teleopit-side layer stays null in
    /// live runs — the two layers must not stack.
    /// </summary>
    public static class BodyMountCorrection
    {
        [Serializable]
        public class SideParams
        {
            public float yaw;
            public float level;
            public Vector3 translation;

            public SideParams Copy() => new SideParams
            {
                yaw = yaw,
                level = level,
                translation = translation,
            };
        }

        [Serializable]
        private class Config
        {
            public bool enabled = true;
            public SideParams left = new SideParams();
            public SideParams right = new SideParams();
        }

        private const int LeftWristRole = (int)BodyTrackerRole.LEFT_WRIST;   // 20
        private const int RightWristRole = (int)BodyTrackerRole.RIGHT_WRIST; // 21
        private const int LeftHandRole = (int)BodyTrackerRole.LEFT_HAND;     // 22
        private const int RightHandRole = (int)BodyTrackerRole.RIGHT_HAND;   // 23

        private static readonly object _stateLock = new object();
        private static Config _config = new Config();
        private static string _path;

        /// <summary>Cache the persistent path + load the stored config. Main thread only
        /// (Application.persistentDataPath); call once at manager init.</summary>
        public static void EnsureLoaded()
        {
            lock (_stateLock)
            {
                _path = Path.Combine(Application.persistentDataPath, "body_mount_correction.json");
                LoadLocked();
            }
        }

        /// <summary>Test seam: point persistence at a scratch file and reset to defaults.</summary>
        public static void ResetForTest(string path)
        {
            lock (_stateLock)
            {
                _path = path;
                _config = new Config();
            }
        }

        /// <summary>Test seam: point persistence at a scratch file and load it.</summary>
        public static void LoadForTest(string path)
        {
            lock (_stateLock)
            {
                _path = path;
                LoadLocked();
            }
        }

        public static bool Enabled
        {
            get
            {
                lock (_stateLock) return _config.enabled;
            }
        }

        /// <summary>Current yaw/level/translation for one side (panel display, t08).</summary>
        public static SideParams GetSide(string side)
        {
            lock (_stateLock)
            {
                var entry = Side(side);
                return entry == null ? null : entry.Copy();
            }
        }

        /// <summary>Enable/disable the correction (t10 gloves/held toggle) and persist.</summary>
        public static void SetEnabled(bool enabled)
        {
            lock (_stateLock)
            {
                _config.enabled = enabled;
                SaveLocked();
            }
        }

        /// <summary>Set one side's yaw/level (degrees) and persist. Unknown side: no-op.</summary>
        public static void SetSide(string side, float yaw, float level)
        {
            lock (_stateLock)
            {
                var entry = Side(side);
                if (entry == null)
                    return;
                entry.yaw = yaw;
                entry.level = level;
                SaveLocked();
            }
        }

        /// <summary>Apply the correction to one body joint (Wrist/Hand roles only).
        /// Returns true when the pose was modified.</summary>
        public static bool Apply(int role, ref Vector3 pos, ref Quaternion rot)
        {
            SideParams entry;
            bool enabled;
            lock (_stateLock)
            {
                enabled = _config.enabled;
                entry = Side(RoleToSide(role));
            }
            if (!enabled || entry == null)
                return false;

            // p' = p - q' * t is evaluated after the rotation so the Wrist ->
            // Hand segment stays rigid under the same translation constant.
            rot = rot * Quaternion.Euler(entry.level, entry.yaw, 0f);
            pos -= rot * entry.translation;
            return true;
        }

        public static string DescribeConfigPath()
        {
            lock (_stateLock)
            {
                return _path ?? "(not loaded)";
            }
        }

        private static string RoleToSide(int role)
        {
            if (role == LeftWristRole || role == LeftHandRole)
                return "left";
            if (role == RightWristRole || role == RightHandRole)
                return "right";
            return null;
        }

        private static SideParams Side(string side)
        {
            if (side == "left")
                return _config.left;
            if (side == "right")
                return _config.right;
            return null;
        }

        private static void LoadLocked()
        {
            try
            {
                if (_path != null && File.Exists(_path))
                    _config = JsonUtility.FromJson<Config>(File.ReadAllText(_path)) ?? new Config();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Mount-correction config unreadable, using defaults: {e.Message}");
                _config = new Config();
            }
        }

        private static void SaveLocked()
        {
            if (_path == null)
                return; // EnsureLoaded not called yet; in-memory only
            try
            {
                File.WriteAllText(_path, JsonUtility.ToJson(_config, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Mount-correction config save failed: {e.Message}");
            }
        }
    }
}
