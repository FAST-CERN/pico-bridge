#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Batch-mode wire dump: writes MockTrackingData frames to a JSONL file so
    /// the pc_receiver parse chain (PicoFrame.from_tracking_payload) can be
    /// exercised without a device (mocap map t03 smoke). Doubles as a compile
    /// check for tracking-side changes. Usage:
    ///   Unity.exe -batchmode -nographics -projectPath ... ^
    ///     -executeMethod PicoBridge.Editor.PicoBridgeMockDump.Dump ^
    ///     -picoBridgeMockDumpPath out.jsonl -picoBridgeMockDumpFrames 5
    /// </summary>
    public static class PicoBridgeMockDump
    {
        private const string PathArg = "-picoBridgeMockDumpPath";
        private const string FramesArg = "-picoBridgeMockDumpFrames";

        public static void Dump()
        {
            string path = GetArg(PathArg, "mock-tracking-dump.jsonl");
            if (!int.TryParse(GetArg(FramesArg, "5"), out int frames) || frames < 1)
                frames = 5;

            using (var writer = new StreamWriter(path))
            {
                for (int i = 0; i < frames; i++)
                    writer.WriteLine(MockTrackingData.GenerateJson(i * 0.1f));
            }

            Debug.Log($"[PicoBridge] Mock tracking dump written: {path} ({frames} frames)");
        }

        private static string GetArg(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1];
            return fallback;
        }
    }
}
#endif
