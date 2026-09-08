using System;
using System.IO;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Append-only JSONL log of raw tracking samples (tracker-ik map t14
    /// infra, kept as generic observability after the solve paradigm was
    /// retired in t17). On device chatty expires ~5000 UnityMain log lines
    /// per 10 s, so Debug.Log is not an observability channel — anything
    /// that must survive a device round lands here. Current writers: none
    /// in-app (the calibration session that fed it is gone); the seam
    /// exists for the next producer (e.g. trim-adjustment capture).
    /// </summary>
    public static class TrackerCalibrationSampleLog
    {
        [Serializable]
        public class PoseRecord
        {
            public float px, py, pz;
            public float qx, qy, qz, qw;
        }

        [Serializable]
        public class SampleLine
        {
            public string type = "sample";
            public int session;
            public string t = "";   // wall clock ISO-8601
            public string kind = "";// producer-defined label
            public PoseRecord puck = new PoseRecord();
            public PoseRecord target = new PoseRecord();
            public PoseRecord head = new PoseRecord();
        }

        private static readonly object _lock = new object();
        private static string _path;

        /// <summary>Main-thread init (Application.persistentDataPath);
        /// PicoBridgeManager calls this beside the calibration store
        /// load. Null path = no-op writes: editor smokes that never call
        /// the seam stay hermetic.</summary>
        public static void EnsureLoaded()
        {
            lock (_lock)
                _path = Path.Combine(Application.persistentDataPath, "tracker_calibration_samples.jsonl");
        }

        /// <summary>Test seam: point at a scratch file (truncated).</summary>
        public static void ResetForTest(string path)
        {
            lock (_lock)
            {
                _path = path;
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        public static void AppendSample(int session, string kind,
            Vector3 puckPos, Quaternion puckRot,
            Vector3 targetPos, Quaternion targetRot,
            Vector3 headPos, Quaternion headRot)
        {
            var line = new SampleLine
            {
                session = session,
                t = DateTime.UtcNow.ToString("o"),
                kind = kind,
                puck = Pose(puckPos, puckRot),
                target = Pose(targetPos, targetRot),
                head = Pose(headPos, headRot),
            };
            Append(JsonUtility.ToJson(line));
        }

        private static PoseRecord Pose(Vector3 pos, Quaternion rot) => new PoseRecord
        {
            px = pos.x, py = pos.y, pz = pos.z,
            qx = rot.x, qy = rot.y, qz = rot.z, qw = rot.w,
        };

        private static void Append(string json)
        {
            lock (_lock)
            {
                if (_path == null)
                    return;
                try
                {
                    File.AppendAllText(_path, json + "\n");
                }
                catch (Exception e)
                {
                    // Logging must never take the app down.
                    Debug.LogWarning($"[PicoBridge] Calibration sample log append failed: {e.Message}");
                }
            }
        }
    }
}
