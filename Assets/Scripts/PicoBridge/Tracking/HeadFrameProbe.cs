using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Head-source frame-relationship probe (tracker-ik map t15): samples
    /// BOTH head pose sources at the same tick — the XR camera (the
    /// calibration head source) and the PXR predicted sensor (the native
    /// pose) — and records per-frame delta = native ∘ unity⁻¹ to an
    /// append-only JSONL. If the two sources differ by a CONSTANT frame
    /// rotation, delta is the same rotation every line; if they differ
    /// only by translation, delta rotation is identity with a growing
    /// position residual; if neither holds, the head source itself is
    /// unstable. Remote-triggered (tracking/set_head_probe) so the
    /// operator wears the headset and moves the head through the window.
    /// Offline adjudication (t17) reads this file; the device-side
    /// summary line is a convenience, not the analysis.
    /// </summary>
    public static class HeadFrameProbe
    {
        public delegate bool UnityHeadDelegate(out Vector3 position, out Quaternion rotation);
        public delegate bool NativeHeadDelegate(out Vector3 position, out Quaternion rotation);

        /// <summary>XR camera head pose (the calibration head source).</summary>
        public static UnityHeadDelegate UnitySource;

        /// <summary>PXR native predicted sensor pose (the 3-arg manager
        /// form adapts to this; the sensor timestamp is dropped).</summary>
        public static NativeHeadDelegate NativeSource;

        /// <summary>Injectable clock (seconds, monotonic). The default is
        /// safe from background threads (Begin arrives on the TCP receive
        /// thread); Tick runs on the main thread.</summary>
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;

        [Serializable]
        private class SampleLine
        {
            public string type = "probe_sample";
            public string t = "";   // wall clock ISO-8601
            public float elapsed;   // seconds since window open
            public TrackerCalibrationSampleLog.PoseRecord unity = new TrackerCalibrationSampleLog.PoseRecord();
            public TrackerCalibrationSampleLog.PoseRecord native = new TrackerCalibrationSampleLog.PoseRecord();
            public TrackerCalibrationSampleLog.PoseRecord delta = new TrackerCalibrationSampleLog.PoseRecord();
        }

        [Serializable]
        private class SummaryLine
        {
            public string type = "probe_summary";
            public string t = "";
            public int n;
            public float durS;
            public float angMeanDeg;    // F_i vs chordal-mean F̄
            public float angSpreadDeg;  // max angle(F_i, F̄) — constant-frame signature is ~0
            public float offResMean;    // mean |native − F̄·unity| — the frame translation magnitude
            public float offResMax;
        }

        private static readonly object _lock = new object();
        private static string _path;
        private static bool _running;
        private static bool _stopRequested;
        private static double _start;
        private static double _deadline;
        private static int _samplesThisWindow;
        private static readonly List<Quaternion> _rots = new List<Quaternion>();
        private static readonly List<Vector3> _unityPos = new List<Vector3>();
        private static readonly List<Vector3> _nativePos = new List<Vector3>();

        public static bool IsRunning
        {
            get { lock (_lock) return _running; }
        }

        /// <summary>Accepted samples in the current (or just-finished)
        /// window — the remote ack's observability channel.</summary>
        public static int SampleCount
        {
            get { lock (_lock) return _samplesThisWindow; }
        }

        /// <summary>Open a sampling window (default 5 s; non-positive or
        /// tiny durations fall back to the default — a remote junk
        /// payload must not open a zero-length window). Thread-safe;
        /// source reads stay on the main thread in Tick.</summary>
        public static void Begin(float durationSeconds = 5f)
        {
            lock (_lock)
            {
                var now = Clock();
                _running = true;
                _stopRequested = false;
                _start = now;
                _deadline = now + (durationSeconds > 0.05f ? durationSeconds : 5f);
                _samplesThisWindow = 0;
                _rots.Clear();
                _unityPos.Clear();
                _nativePos.Clear();
            }
        }

        /// <summary>Close the window at the next Tick (remote abort):
        /// flags only, so the summary write stays on the main thread.</summary>
        public static void Stop()
        {
            lock (_lock)
                _stopRequested = true;
        }

        /// <summary>Main-thread per-frame driver: sample both sources while
        /// the window is open; the tick that crosses the deadline (or sees
        /// a Stop request) writes the summary and closes the window.</summary>
        public static void Tick()
        {
            lock (_lock)
            {
                if (!_running)
                    return;
            }

            var now = Clock();
            var unitySrc = UnitySource;
            var nativeSrc = NativeSource;
            if (unitySrc != null && nativeSrc != null &&
                unitySrc(out var uPos, out var uRot) && nativeSrc(out var nPos, out var nRot))
            {
                var deltaRot = nRot * Quaternion.Inverse(uRot);
                var deltaPos = nPos - deltaRot * uPos;
                lock (_lock)
                {
                    if (_running)
                    {
                        _rots.Add(deltaRot);
                        _unityPos.Add(uPos);
                        _nativePos.Add(nPos);
                        _samplesThisWindow++;
                    }
                }
                Append(JsonUtility.ToJson(new SampleLine
                {
                    t = DateTime.UtcNow.ToString("o"),
                    elapsed = (float)(now - _start),
                    unity = Pose(uPos, uRot),
                    native = Pose(nPos, nRot),
                    delta = Pose(deltaPos, deltaRot),
                }));
            }

            lock (_lock)
            {
                if (!_running)
                    return;
                if (!_stopRequested && now < _deadline)
                    return;
                Append(JsonUtility.ToJson(SummaryLocked(now)));
                _running = false;
            }
        }

        /// <summary>Main-thread init (persistentDataPath); null path =
        /// no-op writes so editor smokes stay hermetic without the seam.</summary>
        public static void EnsureLoaded()
        {
            lock (_lock)
                _path = Path.Combine(Application.persistentDataPath, "tracker_head_probe.jsonl");
        }

        /// <summary>Test seam: scratch file (truncated) + idle state.</summary>
        public static void ResetForTest(string path)
        {
            lock (_lock)
            {
                _path = path;
                if (File.Exists(path))
                    File.Delete(path);
                _running = false;
                _stopRequested = false;
                _samplesThisWindow = 0;
                _rots.Clear();
                _unityPos.Clear();
                _nativePos.Clear();
            }
        }

        private static SummaryLine SummaryLocked(double now)
        {
            var line = new SummaryLine
            {
                t = DateTime.UtcNow.ToString("o"),
                n = _samplesThisWindow,
                durS = (float)(now - _start),
            };
            if (_rots.Count == 0)
                return line;

            // Chordal mean with hemisphere canonicalization against the
            // first sample (same convention as the session's R_f mean).
            var sum = new Quaternion(0f, 0f, 0f, 0f);
            var first = _rots[0];
            for (int i = 0; i < _rots.Count; i++)
            {
                var rot = _rots[i];
                if (Quaternion.Dot(first, rot) < 0f)
                    rot = new Quaternion(-rot.x, -rot.y, -rot.z, -rot.w);
                sum = new Quaternion(sum.x + rot.x, sum.y + rot.y, sum.z + rot.z, sum.w + rot.w);
            }
            var mean = Quaternion.Normalize(sum);

            float angSum = 0f, angMax = 0f, offSum = 0f, offMax = 0f;
            for (int i = 0; i < _rots.Count; i++)
            {
                var ang = Quaternion.Angle(_rots[i], mean);
                angSum += ang;
                if (ang > angMax) angMax = ang;
                var off = (_nativePos[i] - mean * _unityPos[i]).magnitude;
                offSum += off;
                if (off > offMax) offMax = off;
            }
            line.angMeanDeg = angSum / _rots.Count;
            line.angSpreadDeg = angMax;
            line.offResMean = offSum / _rots.Count;
            line.offResMax = offMax;
            return line;
        }

        private static TrackerCalibrationSampleLog.PoseRecord Pose(Vector3 pos, Quaternion rot) =>
            new TrackerCalibrationSampleLog.PoseRecord
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
                    // The probe must never take the app down.
                    Debug.LogWarning($"[PicoBridge] Head probe log append failed: {e.Message}");
                }
            }
        }
    }
}
