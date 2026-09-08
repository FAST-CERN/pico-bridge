using System;
using Unity.XR.PXR;
using UnityEngine;

namespace PicoBridge.Tracking
{
    public enum TrackerSideState
    {
        Unbound,       // no SN bound for the side
        Disconnected,  // bound but the tracker is off/out of range
        OpticalLost,   // connected but no fresh valid pose (left HMD view)
        Valid          // fresh optically-valid pose
    }

    /// <summary>
    /// RED stub (tracker-ik map t01): aggregates the binding + pose cache into
    /// per-side states, a compact panel suffix, and the OS-mode guidance
    /// policy. The silent mode query (GetMotionTrackerMode) is a hardcoded
    /// stub in SDK 3.4.0, so mode mismatch is surfaced through the official
    /// system panel: RequestGuidance() calls CheckMotionTrackerNumber(TWO),
    /// which walks the user through switching the tracking mode and
    /// calibrating. Auto-fires at most once per session, and only when the
    /// tracker flow is live (enumeration requested) yet no side is valid.
    /// </summary>
    public static class TrackerSessionStatus
    {
        public struct SideReport
        {
            public string Side;
            public TrackerSideState State;
        }

        public struct Report
        {
            public SideReport Left;
            public SideReport Right;
            public bool AnyValid;
            public bool ShouldPromptGuidance;
        }

        /// <summary>Injectable guidance trigger; default pops the system panel on device.</summary>
        public static Action GuidanceCaller = DefaultGuidance;

        private static bool _guidanceShown;

        public static Report Evaluate()
        {
            var report = new Report
            {
                Left = EvaluateSide("left"),
                Right = EvaluateSide("right"),
            };
            report.AnyValid =
                report.Left.State == TrackerSideState.Valid ||
                report.Right.State == TrackerSideState.Valid;
            report.ShouldPromptGuidance =
                !_guidanceShown && MotionTrackerBinding.IsStarted && !report.AnyValid;
            return report;
        }

        /// <summary>
        /// Compact ASCII panel suffix for the SN row; empty when any side is
        /// valid. ASCII only — the panel's Liberation SDF font has no CJK
        /// glyphs (device round 2026-09-08 rendered zh as tofu boxes).
        /// Vocabulary matches DescribeSides: "!" = trouble, "?" = optical.
        /// </summary>
        public static string PanelSuffix()
        {
            var report = Evaluate();
            if (report.AnyValid)
                return "";
            if (report.Left.State == TrackerSideState.Unbound || report.Right.State == TrackerSideState.Unbound)
                return " !bind";
            if (report.Left.State == TrackerSideState.Disconnected || report.Right.State == TrackerSideState.Disconnected)
                return " !conn";
            return " !view";
        }

        /// <summary>Human prompt for one side's state (zh, operator-facing).</summary>
        public static string PromptText(TrackerSideState state)
        {
            switch (state)
            {
                case TrackerSideState.Unbound: return "追踪器未绑定：先开左、再开右（自动指认）";
                case TrackerSideState.Disconnected: return "追踪器断连：检查电源或重新开机";
                case TrackerSideState.OpticalLost: return "光学丢追：追踪器回到头显可见范围";
                case TrackerSideState.Valid: return "追踪数据有效";
                default: return "";
            }
        }

        /// <summary>Fire the official guidance panel (explicit calls always fire; the
        /// once-per-session latch only gates the poller's auto path).</summary>
        public static void RequestGuidance()
        {
            _guidanceShown = true;
            GuidanceCaller?.Invoke();
        }

        /// <summary>Test seam: clear the once-per-session guidance latch.</summary>
        public static void ResetForTest()
        {
            _guidanceShown = false;
            GuidanceCaller = DefaultGuidance;
        }

        private static SideReport EvaluateSide(string side)
        {
            var report = new SideReport { Side = side, State = TrackerSideState.Unbound };

            if (!MotionTrackerBinding.TryGetBoundSn(side, out long sn))
                return report;
            if (!MotionTrackerBinding.IsSnConnected(sn))
            {
                report.State = TrackerSideState.Disconnected;
                return report;
            }

            if (TrackerFrameCache.TryGetFrame(side, out var frame) &&
                TrackerFrameCache.IsFresh(frame, TrackerFrameCache.Clock()) && frame.Valid)
            {
                report.State = TrackerSideState.Valid;
            }
            else
            {
                report.State = TrackerSideState.OpticalLost;
            }
            return report;
        }

        private static void DefaultGuidance()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PXR_MotionTracking.CheckMotionTrackerNumber(MotionTrackerNum.TWO);
#endif
        }
    }
}
