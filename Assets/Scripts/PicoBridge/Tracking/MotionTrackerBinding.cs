using System.Collections.Generic;
using System.IO;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Side binding for the two PICO motion trackers (upper-body mocap map t03).
    ///
    /// Startup enumeration goes through CheckMotionTrackerNumber(TWO) whose
    /// result arrives on RequestMotionTrackerCompleteAction — the connection
    /// callback only reports *changes*, not the already-connected set (t01
    /// research). A newly seen SN auto-binds to the first free side (left,
    /// then right) and persists to
    /// persistentDataPath/motion_tracker_binding.json. Power the trackers on
    /// one at a time (left first) to assign sides; a wrong assignment is
    /// fixed by adb-pushing a corrected file (or deleting it to re-assign).
    ///
    /// SDK callbacks may fire off the main thread; shared state is mutated
    /// from those threads the same way TrackingSignalStatus does it.
    /// </summary>
    public static class MotionTrackerBinding
    {
        private const string BindingFileName = "motion_tracker_binding.json";
        private const long Unbound = -1;

        private static readonly HashSet<long> Connected = new HashSet<long>();
        private static bool _subscribed;
        private static bool _started;
        private static long _leftSn = Unbound;
        private static long _rightSn = Unbound;

        public static string BindingPath =>
            Path.Combine(Application.persistentDataPath, BindingFileName);

        /// <summary>
        /// Subscribe connection events and load the persisted binding. Call
        /// at app start (manager Start): trackers that connected before the
        /// bridge session emits Motion (sendMotion off until the receiver
        /// asks) would otherwise be missed — the runtime delivers their
        /// connection events once, right after startup (HITL 2026-09-04:
        /// a pre-connected tracker only bound after a power-cycle until this
        /// moved early).
        /// </summary>
        public static void EnsureSubscribed()
        {
            if (_subscribed)
                return;
            _subscribed = true;

            LoadBinding();
            PXR_MotionTracking.MotionTrackerConnectionAction += OnConnectionChanged;

            Debug.Log(
                "[PicoBridge] Motion tracker events subscribed: " +
                $"left={SnText(_leftSn)} right={SnText(_rightSn)} file={BindingPath}");
        }

        /// <summary>
        /// Idempotent full startup (first Motion frame): on top of the early
        /// subscription, request enumeration of already-connected trackers
        /// via CheckMotionTrackerNumber(TWO) -> RequestMotionTrackerCompleteAction.
        /// </summary>
        public static void EnsureStarted()
        {
            if (_started)
                return;
            _started = true;

            EnsureSubscribed();
            PXR_MotionTracking.RequestMotionTrackerCompleteAction += OnRequestComplete;
            PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);

            Debug.Log("[PicoBridge] Motion tracker binding started (enumeration requested)");
        }

        /// <summary>Bound SN for the side, connected right now.</summary>
        public static bool TryGetConnectedSn(string side, out long sn)
        {
            sn = side == "left" ? _leftSn : _rightSn;
            return sn != Unbound && Connected.Contains(sn);
        }

        /// <summary>
        /// Compact binding summary for the panel (t07): "L:1 R:2" when both
        /// bound and connected, dashes for unbound, "!" suffix when bound but
        /// currently disconnected, "?" suffix when optically invalid (out of
        /// HMD view — fed by MotionTrackerVisualizer).
        /// </summary>
        public static string DescribeSides()
        {
            return $"L:{SideText(_leftSn, "left")} R:{SideText(_rightSn, "right")}";
        }

        private static string SideText(long sn, string side)
        {
            if (sn == Unbound)
                return "--";
            if (!Connected.Contains(sn))
                return sn + "!";
            return _opticalValid.TryGetValue(side, out bool valid) && !valid ? sn + "?" : sn.ToString();
        }

        /// <summary>Latest optical-validity sample per side (visualizer feed).</summary>
        public static void SetOpticalSample(string side, bool valid)
        {
            _opticalValid[side] = valid;
        }

        private static readonly Dictionary<string, bool> _opticalValid = new Dictionary<string, bool>();

        private static void OnRequestComplete(RequestMotionTrackerCompleteEventData data)
        {
            if (data.result != PxrResult.SUCCESS)
            {
                Debug.LogWarning($"[PicoBridge] Motion tracker enumeration failed: {data.result}");
                return;
            }

            int count = Mathf.Min((int)data.trackerCount, data.trackerIds != null ? data.trackerIds.Length : 0);
            for (int i = 0; i < count; i++)
                HandleTrackerConnected(data.trackerIds[i]);
        }

        private static void OnConnectionChanged(long trackerId, int state)
        {
            if (state == 1)
            {
                HandleTrackerConnected(trackerId);
            }
            else
            {
                if (Connected.Remove(trackerId))
                    Debug.Log($"[PicoBridge] Motion tracker disconnected: SN {trackerId}");
            }
        }

        private static void HandleTrackerConnected(long trackerId)
        {
            if (!Connected.Add(trackerId))
                return;

            Debug.Log($"[PicoBridge] Motion tracker connected: SN {trackerId}");

            if (_leftSn == Unbound)
            {
                _leftSn = trackerId;
                SaveBinding();
                Debug.Log($"[PicoBridge] Motion tracker SN {trackerId} bound to LEFT (first free side)");
            }
            else if (_rightSn == Unbound && trackerId != _leftSn)
            {
                _rightSn = trackerId;
                SaveBinding();
                Debug.Log($"[PicoBridge] Motion tracker SN {trackerId} bound to RIGHT");
            }
        }

        private static void LoadBinding()
        {
            try
            {
                if (!File.Exists(BindingPath))
                    return;

                var data = JsonUtility.FromJson<BindingData>(File.ReadAllText(BindingPath));
                if (data == null)
                    return;

                if (data.left != Unbound && data.left != _rightSn)
                    _leftSn = data.left;
                if (data.right != Unbound && data.right != _leftSn)
                    _rightSn = data.right;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Motion tracker binding load failed ({BindingPath}): {e.Message} — falling back to auto-assign");
            }
        }

        private static void SaveBinding()
        {
            try
            {
                File.WriteAllText(BindingPath, JsonUtility.ToJson(new BindingData { left = _leftSn, right = _rightSn }, true));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PicoBridge] Motion tracker binding save failed ({BindingPath}): {e.Message}");
            }
        }

        private static string SnText(long sn) => sn == Unbound ? "unbound" : sn.ToString();

        [System.Serializable]
        private class BindingData
        {
            public long left = Unbound;
            public long right = Unbound;
        }
    }
}
