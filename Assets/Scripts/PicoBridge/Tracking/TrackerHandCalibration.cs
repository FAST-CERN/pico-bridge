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
            public float qx, qy, qz, qw; // R
            public float tx, ty, tz;     // t
            public float positionRms;    // solve quality at commit time
            public float rotationRmsDeg;
            public string poseSet;       // guided set id; null = never calibrated
        }

        [Serializable]
        private class Config
        {
            public SideParams left = new SideParams();
            public SideParams right = new SideParams();
        }

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

        /// <summary>Map a puck pose onto the calibrated hand pose. False when
        /// this side has no calibration (consumers fall back).</summary>
        public static bool TryMap(string side, Vector3 puckPos, Quaternion puckRot, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;
            lock (_stateLock)
            {
                var entry = Side(side);
                if (entry == null || entry.poseSet == null)
                    return false;
                var r = new Quaternion(entry.qx, entry.qy, entry.qz, entry.qw);
                pos = r * puckPos + new Vector3(entry.tx, entry.ty, entry.tz);
                rot = r * puckRot;
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
                    _config = JsonUtility.FromJson<Config>(File.ReadAllText(_path)) ?? new Config();
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
                File.WriteAllText(_path, JsonUtility.ToJson(_config, prettyPrint: true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Tracker-hand calibration save failed: {e.Message}");
            }
        }
    }
}
