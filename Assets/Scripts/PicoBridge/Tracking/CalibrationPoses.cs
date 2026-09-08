using UnityEngine;

namespace PicoBridge.Tracking
{
    /// <summary>
    /// The three guided reference poses for hand calibration (tracker-ik map
    /// t05, Q1): chest / side raise / front raise, defined HEAD-RELATIVE
    /// (HMD-local position offset + orientation) with both sides written out
    /// explicitly. At capture time the session composes
    /// target_stage = head_pose(Unity frame) ∘ target_local, which is the
    /// same frame as the tracker cache — no flip math anywhere.
    ///
    /// Values are first-pass anthropometric defaults (a ~1.75 m operator);
    /// orientations encode a rough palm convention. The DEVICE round owns
    /// tuning these — the smoke asserts structure (three poses, mirrored
    /// positions, non-collinear spread), never the numbers.
    /// </summary>
    public static class CalibrationPoses
    {
        public const string LeftSide = "left";
        public const string RightSide = "right";

        private struct PoseDef
        {
            public string Name;
            public string Instruction;
            public Vector3 LeftPosition;
            public Vector3 LeftEulerDegrees;
            public Vector3 RightEulerDegrees;
        }

        private static readonly PoseDef[] Poses =
        {
            // Hand at the chest, palm toward the body.
            new PoseDef
            {
                Name = "chest",
                Instruction = "hands at chest, palms toward body",
                LeftPosition = new Vector3(0.22f, -0.35f, 0.42f),
                LeftEulerDegrees = new Vector3(0f, 90f, 0f),
                RightEulerDegrees = new Vector3(0f, -90f, 0f),
            },
            // Arm out to the side, palm down.
            new PoseDef
            {
                Name = "side",
                Instruction = "arms straight out, palms down",
                LeftPosition = new Vector3(0.75f, -0.05f, 0.15f),
                LeftEulerDegrees = new Vector3(0f, 0f, 0f),
                RightEulerDegrees = new Vector3(0f, 180f, 0f),
            },
            // Arm raised to the front, palm inward.
            new PoseDef
            {
                Name = "front",
                Instruction = "arms straight ahead, palms face each other",
                LeftPosition = new Vector3(0.18f, 0.10f, 0.62f),
                LeftEulerDegrees = new Vector3(0f, 0f, 90f),
                RightEulerDegrees = new Vector3(0f, 0f, -90f),
            },
        };

        public static int Count => Poses.Length;

        /// <summary>Guidance copy for pose i (t06 panel/render).</summary>
        public static string Name(int index) =>
            index >= 0 && index < Poses.Length ? Poses[index].Name : "";

        /// <summary>ASCII operator instruction for pose i (t06 panel copy;
        /// the panel font has no CJK glyphs).</summary>
        public static string Instruction(int index) =>
            index >= 0 && index < Poses.Length ? Poses[index].Instruction : "";

        /// <summary>Head-local target pose for pose i on one side
        /// ("left"/"right"); unknown indices or sides yield identity.</summary>
        public static void GetLocalPose(int index, string side, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (index < 0 || index >= Poses.Length)
                return;

            var pose = Poses[index];
            bool right = side == RightSide;
            position = right
                ? new Vector3(-pose.LeftPosition.x, pose.LeftPosition.y, pose.LeftPosition.z)
                : pose.LeftPosition;
            rotation = Quaternion.Euler(right ? pose.RightEulerDegrees : pose.LeftEulerDegrees);
        }
    }
}
