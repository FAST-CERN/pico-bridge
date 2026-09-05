using System.Threading;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Latest corrected body-frame snapshot shared between the collector
    /// (producer, main-thread Update) and the in-app visualizer (consumer,
    /// bodytrack-deploy t09). The collector fills this with the corrected
    /// PICO-NATIVE poses (correction applied pre-flip — the SDK avatar
    /// composes only from native locals); the wire output is the same poses
    /// after the standard flip, so the operator sees exactly what the robot
    /// receives. Empty/absent frames leave the cache stale by timestamp.
    /// </summary>
    public static class BodyFrameCache
    {
        public const int JointCount = 24;

        private static readonly object _lock = new object();
        private static readonly Vector3[] _positions = new Vector3[JointCount];
        private static readonly Quaternion[] _rotations = new Quaternion[JointCount];
        private static bool _hasData;
        private static float _lastUpdate = -1f;

        /// <summary>Monotonic time of the last filled frame; -1 when never.</summary>
        public static float LastUpdate
        {
            get { lock (_lock) return _lastUpdate; }
        }

        public static bool HasData
        {
            get { lock (_lock) return _hasData; }
        }

        /// <summary>Copy the latest joint poses. Returns false when no frame yet.</summary>
        public static bool TryGetFrame(Vector3[] positions, Quaternion[] rotations)
        {
            lock (_lock)
            {
                if (!_hasData)
                    return false;
                for (int i = 0; i < JointCount; i++)
                {
                    positions[i] = _positions[i];
                    rotations[i] = _rotations[i];
                }
                return true;
            }
        }

        /// <summary>Fill one joint (collector loop, in role order).</summary>
        public static void SetJoint(int role, Vector3 position, Quaternion rotation, float timestamp)
        {
            if (role < 0 || role >= JointCount)
                return;
            lock (_lock)
            {
                _positions[role] = position;
                _rotations[role] = rotation;
                _hasData = true;
                _lastUpdate = timestamp;
            }
        }

        /// <summary>Test seam: load a full frame at once.</summary>
        public static void SetFrameForTest(Vector3[] positions, Quaternion[] rotations, float timestamp)
        {
            lock (_lock)
            {
                for (int i = 0; i < JointCount; i++)
                {
                    _positions[i] = positions[i];
                    _rotations[i] = rotations[i];
                }
                _hasData = true;
                _lastUpdate = timestamp;
            }
        }

        /// <summary>Test seam: reset to never-filled.</summary>
        public static void ResetForTest()
        {
            lock (_lock)
            {
                _hasData = false;
                _lastUpdate = -1f;
            }
        }
    }
}
