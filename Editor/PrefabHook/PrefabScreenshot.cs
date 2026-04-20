using System.IO;
using UniMCP.Editor.Logging;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// UI 프리팹을 임시 Canvas + Camera 에 렌더해 PNG 로 저장.
    /// Reviewer 에이전트가 레퍼런스와 생성물을 나란히 비교할 수 있도록 제공
    /// </summary>
    public static class PrefabScreenshot
    {
        public class Result
        {
            public bool success;
            public string error;
            public string screenshotPath;
            public int width;
            public int height;
        }

        /// <summary>
        /// 프리팹을 임시 메모리 Canvas 에 인스턴스화해 Camera 로 렌더링, PNG 저장.
        /// width/height 는 기준 캔버스 해상도 (루트 프리팹의 sizeDelta 를 감안)
        /// </summary>
        public static Result Capture(string prefabPath, string outputPng, int width = 1080, int height = 1920)
        {
            if (string.IsNullOrEmpty(prefabPath) || !File.Exists(prefabPath))
                return Fail("프리팹 경로 없음: " + prefabPath);

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null) return Fail("프리팹 로드 실패");

            GameObject canvasGo = null, cameraGo = null, instance = null;
            RenderTexture rt = null;
            Texture2D tex = null;

            try
            {
                canvasGo = new GameObject("__UniMCP_ScreenshotCanvas",
                    typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster))
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };

                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

                cameraGo = new GameObject("__UniMCP_ScreenshotCamera", typeof(Camera))
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var camera = cameraGo.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.09f, 0.14f, 0.20f, 1f);   // Unity Scene bg 유사
                camera.orthographic = true;
                camera.nearClipPlane = -100f;
                camera.farClipPlane = 1000f;
                camera.orthographicSize = height * 0.5f;
                camera.transform.position = new Vector3(0, 0, -10);

                canvas.worldCamera = camera;
                canvas.planeDistance = 10;

                var canvasRt = (RectTransform)canvasGo.transform;
                canvasRt.sizeDelta = new Vector2(width, height);

                instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, canvas.transform);
                if (instance == null) return Fail("Instantiate 실패");

                // 인스턴스를 중앙에 배치
                if (instance.transform is RectTransform instRt)
                {
                    instRt.anchorMin = new Vector2(0.5f, 0.5f);
                    instRt.anchorMax = new Vector2(0.5f, 0.5f);
                    instRt.pivot = new Vector2(0.5f, 0.5f);
                    instRt.anchoredPosition = Vector2.zero;
                }

                // Layout 이 정착되도록 강제
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)canvas.transform);

                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;
                var prev = RenderTexture.active;
                RenderTexture.active = rt;

                camera.Render();

                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                RenderTexture.active = prev;
                camera.targetTexture = null;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPng));
                File.WriteAllBytes(outputPng, tex.EncodeToPNG());

                return new Result
                {
                    success = true,
                    screenshotPath = outputPng,
                    width = width,
                    height = height,
                };
            }
            catch (System.Exception e)
            {
                UniMcpLogger.Warn("Screenshot 실패: " + e.Message + "\n" + e.StackTrace);
                return Fail(e.Message);
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (instance != null) Object.DestroyImmediate(instance);
                if (canvasGo != null) Object.DestroyImmediate(canvasGo);
                if (cameraGo != null) Object.DestroyImmediate(cameraGo);
            }
        }

        private static Result Fail(string msg) => new() { success = false, error = msg };
    }
}
