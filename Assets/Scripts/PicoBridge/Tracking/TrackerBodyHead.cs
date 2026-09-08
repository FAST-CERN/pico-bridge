using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// Head-pose source for the transitional tracker-mode body frame
    /// (tracker-ik map t03): injectable so editor smokes feed golden
    /// literals while the device reads the same predicted-sensor state
    /// AppendHead uses.
    /// </summary>
    public static class TrackerBodyHead
    {
        public delegate bool ReadHeadPose(out Vector3 position, out Quaternion rotation, out long timestampUs);

        public static ReadHeadPose Source;
    }
}
