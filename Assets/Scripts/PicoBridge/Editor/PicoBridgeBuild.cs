#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PicoBridge.Editor
{
    public static class PicoBridgeBuild
    {
        private const string DefaultAndroidApkPath = "/tmp/pico-bridge.apk";
        private const string BuildPathArg = "-picoBridgeBuildPath";

        // Team-wide signing so every machine produces APKs with the same
        // certificate and `adb install -r` upgrades in place. The keystore is
        // the repo copy of the original debug key that already-installed
        // builds were signed with (standard Android debug credentials).
        private static readonly string SharedKeystorePath =
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "keystores", "picobridge.jks"));
        private const string SharedKeystoreAlias = "androiddebugkey";
        private const string SharedKeystorePass = "android";
        private const string SharedKeyaliasPass = "android";

        public static void BuildAndroidApkFromCommandLine()
        {
            var outputPath = GetArgumentValue(BuildPathArg, DefaultAndroidApkPath);
            if (!File.Exists(SharedKeystorePath))
                throw new InvalidOperationException(
                    $"Shared signing keystore not found: {SharedKeystorePath}. " +
                    "Expected keystores/picobridge.jks in the repository root; " +
                    "see docs/zh/build-and-install.md.");

            var preloadedAssets = PlayerSettings.GetPreloadedAssets();
            var prevUseCustomKeystore = PlayerSettings.Android.useCustomKeystore;
            var prevKeystoreName = PlayerSettings.Android.keystoreName;
            var prevKeyaliasName = PlayerSettings.Android.keyaliasName;
            var prevKeystorePass = PlayerSettings.Android.keystorePass;
            var prevKeyaliasPass = PlayerSettings.Android.keyaliasPass;

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = SharedKeystorePath;
            PlayerSettings.Android.keyaliasName = SharedKeystoreAlias;
            PlayerSettings.Android.keystorePass = SharedKeystorePass;
            PlayerSettings.Android.keyaliasPass = SharedKeyaliasPass;
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
                throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.Android.useCustomKeystore = prevUseCustomKeystore;
                PlayerSettings.Android.keystoreName = prevKeystoreName;
                PlayerSettings.Android.keyaliasName = prevKeyaliasName;
                PlayerSettings.Android.keystorePass = prevKeystorePass;
                PlayerSettings.Android.keyaliasPass = prevKeyaliasPass;
                PlayerSettings.SetPreloadedAssets(preloadedAssets);
            }

            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Android build failed: {report.summary.result}");

            Debug.Log($"[PicoBridge] Android build succeeded: {outputPath}");
        }

        private static string GetArgumentValue(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1];
            }

            return fallback;
        }
    }
}
#endif
