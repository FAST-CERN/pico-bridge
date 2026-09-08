using System;
using System.IO;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Per-side puck→hand mapping store (tracker-ik map t17, human-trim
    /// paradigm). The solve paradigm (global Kabsch → local mount → AX=YB)
    /// was killed by the 2026-09-09 device round: 3 guided poses cannot
    /// constrain 9 DOF (leave-one-out 415-609 mm), the head-vs-cache frame
    /// relation is non-constant (probe 28.4° deviation), and the rotation
    /// conjugacy angles don't match — see the map Notes. What survives is
    /// physical fact: the mount offset is tiny (|m| 2-5 cm) and the only
    /// real unknown is the uncontrolled strap ROTATION.
    ///
    /// So the mapping is a per-side operator TRIM, adjusted once per
    /// strapping (~30 s) against passthrough: yaw/pitch/roll compose in
    /// the puck's local frame, level slides along the trimmed hand's own
    /// vertical axis (same semantics as BodyMountCorrection's level, whose
    /// panel row is the UX template).
    /// </summary>
    public static class TrackerHandCalibration
    {
        [Serializable]
        public class SideParams
        {
            public float yaw;    // degrees, puck-local twist about up
            public float pitch;  // degrees, puck-local tilt about right
            public float roll;   // degrees, puck-local roll about forward
            public float level;  // millimetres slid along the trimmed up axis
        }

        [Serializable]
        private class Config
        {
            // t13 schema gate, carried into the trim paradigm: "" or any
            // unrecognized value = a store from an older model (model-1
            // global {R,t}, axyb-v1 {C,R_f,m}) whose fields do NOT mean
            // trim values — load as defaults (identity mapping), never
            // reinterpret. SaveLocked stamps the current value.
            public string model = "";
            public SideParams left = new SideParams();
            public SideParams right = new SideParams();
        }

        /// <summary>Current store schema (t17 human-trim).</summary>
        private const string StoreModel = "trim-v1";

        private static readonly object _stateLock = new object();
        private static Config _config = new Config();
        private static string _path;

        /// <summary>Cache the persistent path + load the stored config. Main
        /// thread only (Application.persistentDataPath); manager init calls
        /// this once.</summary>
        public static void EnsureLoaded()
        {
            lock (_stateLock)
            {
                _path = Path.Combine(Application.persistentDataPath, "tracker_hand_calibration.json");
                LoadLocked();
            }
        }

        /// <summary>Test seam: point persistence at a scratch file and reset.</summary>
        public static void ResetForTest(string path)
        {
            lock (_stateLock)
            {
                _path = path;
                _config = new Config();
            }
        }

        /// <summary>Test seam: point persistence at a file and load it.</summary>
        public static void LoadForTest(string path)
        {
            lock (_stateLock)
            {
                _path = path;
                LoadLocked();
            }
        }

        /// <summary>One side's stored trim (copy) or null for an unknown
        /// side. Unlike the solve era there is no "uncalibrated" state —
        /// zero trim is a valid mapping (hand ≡ puck).</summary>
        public static SideParams GetSide(string side)
        {
            lock (_stateLock)
            {
                var entry = Side(side);
                return entry == null ? null : Copy(entry);
            }
        }

        /// <summary>Map a puck pose onto the hand pose with the per-side
        /// trim: hand_rot = puck_rot · trim (yaw∘pitch∘roll, puck-local),
        /// hand_pos = puck_pos − hand_rot · (0, level mm, 0) — the same
        /// slide convention as BodyMountCorrection's level knob. The
        /// consumers (MotionTrackerVisualizer now, the t08 IK later) keep
        /// the same signature the solve era had.</summary>
        public static bool TryMap(string side, Vector3 puckPos, Quaternion puckRot, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;
            SideParams entry;
            lock (_stateLock)
                entry = Side(side);
            if (entry == null)
                return false;
            var trim = Quaternion.AngleAxis(entry.yaw, Vector3.up) *
                       Quaternion.AngleAxis(entry.pitch, Vector3.right) *
                       Quaternion.AngleAxis(entry.roll, Vector3.forward);
            rot = puckRot * trim;
            pos = puckPos - rot * new Vector3(0f, entry.level * 0.001f, 0f);
            return true;
        }

        /// <summary>Set one side's trim and persist (panel knob clicks and
        /// the remote set_hand_trim push both land here). Unknown side:
        /// no-op.</summary>
        public static void SetSide(string side, float yaw, float pitch, float roll, float level)
        {
            lock (_stateLock)
            {
                var entry = Side(side);
                if (entry == null)
                    return;
                entry.yaw = yaw;
                entry.pitch = pitch;
                entry.roll = roll;
                entry.level = level;
                SaveLocked();
            }
        }

        private static SideParams Side(string side)
        {
            if (side == "left")
                return _config.left;
            if (side == "right")
                return _config.right;
            return null;
        }

        private static SideParams Copy(SideParams entry) => new SideParams
        {
            yaw = entry.yaw,
            pitch = entry.pitch,
            roll = entry.roll,
            level = entry.level,
        };

        private static void LoadLocked()
        {
            try
            {
                if (_path != null && File.Exists(_path))
                {
                    _config = JsonUtility.FromJson<Config>(File.ReadAllText(_path)) ?? new Config();
                    if (_config.model != StoreModel)
                    {
                        // Schema gate (t13, carried into t17): a store from
                        // another model version is different DATA — reset to
                        // defaults; the first knob click rewrites the file.
                        Debug.LogWarning($"[PicoBridge] Tracker-hand calibration store schema '{_config.model}' != '{StoreModel}' - trim reset to defaults");
                        _config = new Config();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Tracker-hand calibration unreadable, using defaults: {e.Message}");
                _config = new Config();
            }
        }

        private static void SaveLocked()
        {
            if (_path == null)
                return; // EnsureLoaded not called yet; in-memory only
            try
            {
                _config.model = StoreModel; // stamp on every write (t13)
                File.WriteAllText(_path, JsonUtility.ToJson(_config, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Tracker-hand calibration save failed: {e.Message}");
            }
        }
    }
}
