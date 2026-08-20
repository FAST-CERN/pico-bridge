#if UNITY_EDITOR
using UnityEditor;

namespace PicoBridge.Editor
{
    /// <summary>
    /// CLI entry: rebuilds the panel prefab (picking up immersive button)
    /// and refreshes the sample scene, then builds the Android APK.
    /// </summary>
    public static class PicoBridgeStereoBuild
    {
        public static void RebuildPrefabAndBuildApk()
        {
            PicoBridgeSceneUiTemplate.RebuildPanelPrefab();
            PicoBridgeSceneUiTemplate.RebuildSampleSceneUiTemplate();
            PicoBridgeBuild.BuildAndroidApkFromCommandLine();
        }
    }
}
#endif
