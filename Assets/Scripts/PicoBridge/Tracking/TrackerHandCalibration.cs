using System;
using System.IO;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Per-side puck->hand rigid transform store (tracker-ik map t05).
    /// Mirrors the BodyMountCorrection persistence pattern:
    /// persistentDataPath JSON, boot-time load, thread-guarded. A side is
    /// "calibrated" once its poseSet is set (the serializable default leaves
    /// it null, so a fresh install maps nothing).
    ///
    /// Mapping contract (t07/t09 consume): hand_pos = R·puck_pos + t,
    /// hand_rot = R·puck_rot — a single {R,t} covers position and
    /// orientation because the puck is rigidly strapped to the hand.
    /// Everything stays in the Unity frame the tracker cache uses.
    /// </summary>
    public static class TrackerHandCalibration
    {
        [Serializable]
        public class SideParams
        {
            // AX=YB mount model, 2026-09-09: the head source and tracker
            // cache frames differ by a constant rotation R_f (f*), so
            // hand_rot = R_f * puck_rot * C (q*) and
            // hand_pos = R_f * (puck_pos + puck_rot * m) (t*).
            public float qx, qy, qz, qw; // C (right-multiplied mount rotation)
            public float fx, fy, fz, fw; // R_f (left-multiplied frame rotation)
            public float tx, ty, tz;     // m, metres in the puck frame
            public float positionRms;    // solve quality at commit time
            public float rotationRmsDeg;
            public string poseSet;       // guided set id; null = never calibrated
        }

        [Serializable]
        private class Config
        {
            // t13 schema gate: "" or any unrecognized value = a store from
            // an older model (pre-AX=YB global {R,t} / local {C,m}) that
            // this code must NOT reinterpret — on device (09-09) the legacy
            // file read R_f=(0,0,0,0) ≈ identity and flung the mapped hand
            // ~3.2 m with no commit in between. SaveLocked stamps the
            // current value on every write; LoadLocked gates on it.
            public string model = "";
            public SideParams left = new SideParams();
            public SideParams right = new SideParams();
        }

        /// <summary>Current store schema. Bump whenever SideParams
        /// semantics change (model swap = new version string, old stores
        /// load as uncalibrated and viz falls back to puck-only).</summary>
        private const string StoreModel = "axyb-v1";

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

        /// <summary>One side's stored params (copy) or null.</summary>
        public static SideParams GetSide(string side)
        {
            lock (_stateLock)
            {
                var entry = Side(side);
                return entry == null || entry.poseSet == null ? null : Copy(entry);
            }
        }

        /// <summary>Map a puck pose onto the calibrated hand pose with the
        /// AX=YB mount model: hand_rot = R_f * puck_rot * C and
        /// hand_pos = R_f * (puck_pos + puck_rot * m). False when this side
        /// has no calibration (consumers fall back). Earlier formulations: a
        /// GLOBAL R*p+t (cannot follow a mounted puck) and a local-only
        /// compose (cannot absorb the head-source vs cache frame rotation) —
        /// see TrackerCalibrationSession.SolveSideLocked.</summary>
        public static bool TryMap(string side, Vector3 puckPos, Quaternion puckRot, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;
            lock (_stateLock)
            {
                var entry = Side(side);
                if (entry == null || entry.poseSet == null)
                    return false;
                var c = new Quaternion(entry.qx, entry.qy, entry.qz, entry.qw);
                var rf = new Quaternion(entry.fx, entry.fy, entry.fz, entry.fw);
                pos = rf * (puckPos + puckRot * new Vector3(entry.tx, entry.ty, entry.tz));
                rot = rf * puckRot * c;
                return true;
            }
        }

        /// <summary>Commit one side's solved transform and persist. The
        /// session only calls this after BOTH sides passed their gates.</summary>
        public static void Commit(string side, SideParams entry)
        {
            if (entry == null)
                return;
            lock (_stateLock)
            {
                var target = Side(side);
                if (target == null)
                    return;
                target.qx = entry.qx; target.qy = entry.qy; target.qz = entry.qz; target.qw = entry.qw;
                target.fx = entry.fx; target.fy = entry.fy; target.fz = entry.fz; target.fw = entry.fw;
                target.tx = entry.tx; target.ty = entry.ty; target.tz = entry.tz;
                target.positionRms = entry.positionRms;
                target.rotationRmsDeg = entry.rotationRmsDeg;
                target.poseSet = entry.poseSet;
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
            qx = entry.qx, qy = entry.qy, qz = entry.qz, qw = entry.qw,
            fx = entry.fx, fy = entry.fy, fz = entry.fz, fw = entry.fw,
            tx = entry.tx, ty = entry.ty, tz = entry.tz,
            positionRms = entry.positionRms,
            rotationRmsDeg = entry.rotationRmsDeg,
            poseSet = entry.poseSet,
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
                        // Schema gate (t13): a store from another model
                        // version is different DATA, not a config tweak —
                        // fields shift meaning between schemas. Treat both
                        // sides as uncalibrated; the next round rewrites
                        // the file under the current schema.
                        Debug.LogWarning($"[PicoBridge] Tracker-hand calibration store schema '{_config.model}' != '{StoreModel}' - treating as uncalibrated, recalibrate");
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
