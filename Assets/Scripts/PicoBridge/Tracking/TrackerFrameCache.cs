using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>One side's latest tracker sample in the pico_tracker_local frame.</summary>
    public struct TrackerSideFrame
    {
        public long Sn;
        public Vector3 Position;    // flipped (-Z) tracker-local position
        public Quaternion Rotation; // flipped (-Qz, -Qw) tracker-local rotation
        public bool HasPose;        // any optically-valid pose ever published
        public bool Valid;          // latest optical validity
        public float LastUpdate;    // publish time (Clock domain)
    }

    /// <summary>
    /// Latest per-side tracker pose: the single device-side acquisition view
    /// (tracker-ik map t01) consumed by the wire serializer, the in-headset
    /// gizmos, and (later) calibration/IK. Poses are stored already flipped
    /// into pico_tracker_local (−Z, −Qz, −Qw — the frame the wire contract
    /// and the gizmo axes share, t01 §4), so every consumer reads one frame.
    /// A side that stops being published goes stale and reads as absent.
    /// Mirrors BodyFrameCache's producer/consumer shape.
    /// </summary>
    public static class TrackerFrameCache
    {
        public const float StaleAfter = 0.5f;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, TrackerSideFrame> _sides =
            new Dictionary<string, TrackerSideFrame>();

        /// <summary>Clock source for freshness; overridable in editor tests.</summary>
        public static Func<float> Clock = () => Time.unscaledTime;

        public static bool TryGetFrame(string side, out TrackerSideFrame frame)
        {
            lock (_lock)
                return _sides.TryGetValue(side, out frame);
        }

        public static bool IsFresh(TrackerSideFrame frame, float now)
        {
            return now - frame.LastUpdate <= StaleAfter;
        }

        /// <summary>Publish an optically-valid sample (pose moves, validity on).</summary>
        public static void PublishValid(string side, long sn, Vector3 p, Quaternion q, float t)
        {
            lock (_lock)
            {
                _sides[side] = new TrackerSideFrame
                {
                    Sn = sn,
                    Position = p,
                    Rotation = q,
                    HasPose = true,
                    Valid = true,
                    LastUpdate = t,
                };
            }
        }

        /// <summary>Publish an invalid sample (validity off, last pose kept for the ghost).</summary>
        public static void PublishInvalid(string side, long sn, float t)
        {
            lock (_lock)
            {
                _sides.TryGetValue(side, out var frame);
                frame.Sn = sn;
                frame.Valid = false;
                frame.LastUpdate = t;
                _sides[side] = frame;
            }
        }

        /// <summary>Test seam: reset to never-published.</summary>
        public static void ResetForTest()
        {
            lock (_lock) _sides.Clear();
        }
    }
}
