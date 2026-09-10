#if UNITY_EDITOR
using System;
using System.IO;
using PicoBridge.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PicoBridge.Editor
{
    public static class PicoBridgeAudioLayoutCheck
    {
        public static void Run()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab");
            var canvasObject = new GameObject("Audio layout check", typeof(RectTransform), typeof(Canvas));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObject.layer = LayerMask.NameToLayer("UI");
            canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(860, 368);
            var panel = UnityEngine.Object.Instantiate(prefab, canvasObject.transform);
            var root = panel.GetComponent<RectTransform>();
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            var view = panel.GetComponent<PicoBridgePanelView>();
            view.cameraPreviewRoot.gameObject.SetActive(false);
            view.rootCanvasGroup.alpha = 1f;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            Canvas.ForceUpdateCanvases();
            foreach (var control in new RectTransform[] {
                view.audioButton.GetComponent<RectTransform>(),
                view.microphoneMuteButton.GetComponent<RectTransform>(), view.audioStatusText.rectTransform })
            {
                Debug.Log("[PicoAudioLayout] " + control.name + " rect=" + control.rect + " active=" + control.gameObject.activeInHierarchy);
                if (control.rect.width < 20 || control.rect.height < 20) throw new Exception("Audio control has no usable area: " + control.name);
                var corners = new Vector3[4];
                control.GetWorldCorners(corners);
                foreach (var point in corners)
                {
                    Vector3 local = root.InverseTransformPoint(point);
                    if (local.x < root.rect.xMin - 1 || local.x > root.rect.xMax + 1
                        || local.y < root.rect.yMin - 1 || local.y > root.rect.yMax + 1)
                        throw new Exception("Audio control extends outside compact panel: " + control.name);
                }
            }
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-picoAudioPreviewPath");
            if (index >= 0 && index + 1 < args.Length)
            {
                var cameraObject = new GameObject("Audio layout camera", typeof(UnityEngine.Camera));
                var camera = cameraObject.GetComponent<UnityEngine.Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
                camera.orthographic = true;
                camera.orthographicSize = 210;
                camera.backgroundColor = new Color(0.02f, 0.025f, 0.03f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.cullingMask = 1 << LayerMask.NameToLayer("UI");
                canvas.worldCamera = camera;
                Canvas.ForceUpdateCanvases();
                var target = new RenderTexture(1200, 560, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                var texture = new Texture2D(1200, 560, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, 1200, 560), 0, 0);
                texture.Apply();
                File.WriteAllBytes(args[index + 1], texture.EncodeToPNG());
                RenderTexture.active = null;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
            UnityEngine.Object.DestroyImmediate(canvasObject);
            Debug.Log("[PicoAudioLayout] PASS: audio controls fit the compact panel.");
        }
    }
}
#endif
