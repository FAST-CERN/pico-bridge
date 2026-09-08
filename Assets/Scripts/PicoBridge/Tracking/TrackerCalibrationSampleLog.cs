using System;
using System.IO;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Append-only JSONL log of raw calibration samples (tracker-ik map
    /// t14). On device chatty expires ~5000 UnityMain log lines per 10 s,
    /// so every Debug.Log observability line (residuals, R_f angle, reject
    /// reasons) is unrecoverable — this file is the only surviving record
    /// of a round. One line per captured pose (both sides' raw puck +
    /// target + head poses) plus one event line per solve (committed with
    /// both solutions / rejected with the reason), so a whole round can be
    /// replayed offline — the t16/t17 overfit-vs-frame-rotation-vs-literal
    /// adjudication runs on this data.
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
            public int poseIndex;
            public string side = "";
            public PoseRecord puck = new PoseRecord();
            public PoseRecord target = new PoseRecord();
            public PoseRecord head = new PoseRecord();
        }

        [Serializable]
        public class SideLine
        {
            public float[] c = new float[4];   // solved mount quat
            public float[] rf = new float[4];  // solved frame quat
            public float[] m = new float[3];   // solved mount offset (puck frame, m)
            public float posRms;
            public float rotRmsDeg;
        }

        [Serializable]
        public class EventLine
        {
            public string type = "";           // "committed" | "rejected"
            public int session;
            public string t = "";
            public string reason = "";         // rejected only
            public SideLine left;
            public SideLine right;
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

        public static void AppendSample(int session, int poseIndex, string side,
            Vector3 puckPos, Quaternion puckRot,
            Vector3 targetPos, Quaternion targetRot,
            Vector3 headPos, Quaternion headRot)
        {
            var line = new SampleLine
            {
                session = session,
                t = DateTime.UtcNow.ToString("o"),
                poseIndex = poseIndex,
                side = side,
                puck = Pose(puckPos, puckRot),
                target = Pose(targetPos, targetRot),
                head = Pose(headPos, headRot),
            };
            Append(JsonUtility.ToJson(line));
        }

        public static void AppendCommitted(
            int session, TrackerHandCalibration.SideParams left,
            TrackerHandCalibration.SideParams right)
        {
            Append(JsonUtility.ToJson(new EventLine
            {
                type = "committed",
                session = session,
                t = DateTime.UtcNow.ToString("o"),
                left = Side(left),
                right = Side(right),
            }));
        }

        public static void AppendRejected(int session, string reason)
        {
            Append(JsonUtility.ToJson(new EventLine
            {
                type = "rejected",
                session = session,
                t = DateTime.UtcNow.ToString("o"),
                reason = reason ?? "",
            }));
        }

        private static PoseRecord Pose(Vector3 pos, Quaternion rot) => new PoseRecord
        {
            px = pos.x, py = pos.y, pz = pos.z,
            qx = rot.x, qy = rot.y, qz = rot.z, qw = rot.w,
        };

        private static SideLine Side(TrackerHandCalibration.SideParams p) => new SideLine
        {
            c = new[] { p.qx, p.qy, p.qz, p.qw },
            rf = new[] { p.fx, p.fy, p.fz, p.fw },
            m = new[] { p.tx, p.ty, p.tz },
            posRms = p.positionRms,
            rotRmsDeg = p.rotationRmsDeg,
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
                    // Logging must never take a calibration round down.
                    Debug.LogWarning($"[PicoBridge] Calibration sample log append failed: {e.Message}");
                }
            }
        }
    }
}
