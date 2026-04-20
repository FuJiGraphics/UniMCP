// JsonUtility 리플렉션으로 채워지는 args 필드들은 컴파일러가 미할당으로 오진
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UniMCP.Editor.Logging;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// 파일 큐 기반 Unity UI 프리팹 조작 훅 실행기.
    /// Library/UniMCP/PrefabHook/cmd 에 들어오는 커맨드를 폴링해 Unity API 로 실행하고 res 에 응답을 쓴다.
    /// 단일 편집 세션(동시 1개 프리팹)만 지원
    /// </summary>
    [InitializeOnLoad]
    public static class PrefabHookExecutor
    {
        private static GameObject _sessionRoot;
        private static string _sessionPath;
        private static double _lastPoll;
        private const double PollInterval = 0.25;

        private static string HookRoot
        {
            get
            {
                var project = Path.GetDirectoryName(Application.dataPath);
                return Path.Combine(project, "Library", "UniMCP", "PrefabHook");
            }
        }

        private static string CmdDir => Path.Combine(HookRoot, "cmd");
        private static string ResDir => Path.Combine(HookRoot, "res");

        /// <summary>
        /// 현재 실행 중인 작업의 로그 파일 경로. Window/RunQueue 가 Run 시작 시 세팅.
        /// 비어있으면 훅 실행 내역은 로그에 남기지 않는다 (다른 경로에서 호출되는 개별 훅은 로그 불필요)
        /// </summary>
        public static string ActiveLogPath { get; set; }

        private static readonly object _logLock = new();

        public static void AppendLog(string line)
        {
            var path = ActiveLogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
                }
            }
            catch { }
        }

        static PrefabHookExecutor()
        {
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastPoll < PollInterval) return;
            _lastPoll = now;

            if (!Directory.Exists(CmdDir)) return;

            string[] files;
            try { files = Directory.GetFiles(CmdDir, "*.json"); }
            catch { return; }

            foreach (var file in files)
                ProcessCommandFile(file);
        }

        private static int _hookCallCount;

        private static void ProcessCommandFile(string cmdFile)
        {
            var id = Path.GetFileNameWithoutExtension(cmdFile);
            Response res;
            Command cmd = null;
            var callIdx = System.Threading.Interlocked.Increment(ref _hookCallCount);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var json = File.ReadAllText(cmdFile);
                cmd = JsonUtility.FromJson<Command>(json);
                AppendLog($"HOOK #{callIdx}  {cmd.op}");
                AppendLog($"  args     {cmd.args}");
                res = Dispatch(cmd);
            }
            catch (Exception e)
            {
                UniMcpLogger.Warn($"PrefabHook 명령 처리 실패: {e.Message}");
                AppendLog($"  ERROR  dispatch: {e.Message}");
                res = Response.Fail(e.Message);
            }

            sw.Stop();

            if (res.success)
            {
                AppendLog($"  ok       {FormatData(res.data)}  ({sw.ElapsedMilliseconds}ms)");
            }
            else
            {
                AppendLog($"  FAIL     {res.error}  ({sw.ElapsedMilliseconds}ms)");
            }

            try
            {
                Directory.CreateDirectory(ResDir);
                File.WriteAllText(Path.Combine(ResDir, id + ".json"), JsonUtility.ToJson(res));
            }
            catch (Exception e)
            {
                UniMcpLogger.Warn($"PrefabHook 응답 기록 실패: {e.Message}");
            }

            try { File.Delete(cmdFile); } catch { }
        }

        private static string FormatData(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "{}") return "-";
            return s.Replace("\n", " ").Replace("\r", "");
        }

        /// <summary>
        /// 다음 작업 시작 시 훅 호출 번호를 1 부터 다시 시작하도록 리셋
        /// </summary>
        public static void ResetHookCounter() => _hookCallCount = 0;

        private static Response Dispatch(Command cmd)
        {
            switch (cmd.op)
            {
                // Session
                case "create-prefab": return CreatePrefab(cmd.args);
                case "open-prefab":   return OpenPrefab(cmd.args);
                case "save-prefab":   return SavePrefab();
                case "cancel-prefab": return CancelPrefab();

                // Hierarchy
                case "add-child":          return AddChild(cmd.args);
                case "add-nested-prefab":  return AddNestedPrefab(cmd.args);
                case "delete":             return Delete(cmd.args);
                case "rename":             return Rename(cmd.args);
                case "reparent":           return Reparent(cmd.args);
                case "set-sibling-index":  return SetSiblingIndex(cmd.args);
                case "set-active":         return SetActive(cmd.args);
                case "duplicate":          return Duplicate(cmd.args);

                // RectTransform
                case "set-rect":     return SetRect(cmd.args);
                case "set-anchor":   return SetAnchor(cmd.args);
                case "set-pivot":    return SetPivot(cmd.args);
                case "set-rotation": return SetRotation(cmd.args);
                case "set-scale":    return SetScale(cmd.args);

                // Graphics
                case "add-image":          return AddImage(cmd.args);
                case "add-raw-image":      return AddRawImage(cmd.args);
                case "add-tmp":            return AddTmp(cmd.args);
                case "set-image-sprite":   return SetImageSprite(cmd.args);
                case "set-image-color":    return SetImageColor(cmd.args);
                case "set-image-type":     return SetImageType(cmd.args);
                case "set-tmp-text":       return SetTmpText(cmd.args);
                case "set-tmp-font-size":  return SetTmpFontSize(cmd.args);
                case "set-tmp-color":      return SetTmpColor(cmd.args);
                case "set-tmp-alignment":  return SetTmpAlignment(cmd.args);
                case "set-tmp-font":       return SetTmpFont(cmd.args);

                // Interactables
                case "add-button":          return AddComponent<Button>(cmd.args, ensureGraphic: true);
                case "add-toggle":          return AddToggle(cmd.args);
                case "add-slider":          return AddComponent<Slider>(cmd.args);
                case "add-scrollbar":       return AddScrollbar(cmd.args);
                case "add-scroll-rect":     return AddScrollRect(cmd.args);
                case "add-dropdown":        return AddComponent<Dropdown>(cmd.args);
                case "add-tmp-dropdown":    return AddComponent<TMP_Dropdown>(cmd.args);
                case "add-input-field":     return AddComponent<InputField>(cmd.args);
                case "add-tmp-input-field": return AddComponent<TMP_InputField>(cmd.args);

                // Layout
                case "add-horizontal-layout":  return AddHorizontalLayout(cmd.args);
                case "add-vertical-layout":    return AddVerticalLayout(cmd.args);
                case "add-grid-layout":        return AddGridLayout(cmd.args);
                case "add-layout-element":     return AddLayoutElement(cmd.args);
                case "add-content-size-fitter":return AddContentSizeFitter(cmd.args);
                case "add-aspect-ratio-fitter":return AddAspectRatioFitter(cmd.args);

                // Canvas / Mask / Effect
                case "add-canvas":         return AddSubCanvas(cmd.args);
                case "add-canvas-group":   return AddCanvasGroup(cmd.args);
                case "add-mask":           return AddMask(cmd.args);
                case "add-rect-mask-2d":   return AddComponent<RectMask2D>(cmd.args);
                case "add-shadow":         return AddShadow(cmd.args);
                case "add-outline":        return AddOutline(cmd.args);

                // Generic escape hatch
                case "add-component": return AddComponentByName(cmd.args);

                // Introspection
                case "list-children": return ListChildren(cmd.args);
                case "get-rect":      return GetRect(cmd.args);
                case "exists":        return Exists(cmd.args);

                default: return Response.Fail("Unknown op: " + cmd.op);
            }
        }

        #region Session

        [Serializable] private class PathArgs { public string path; }
        [Serializable] private class ChildArgs { public string parent; public string name; }
        [Serializable] private class NestedArgs { public string parent; public string name; public string prefabPath; }

        private static Response CreatePrefab(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a?.path)) return Response.Fail("path 필요");
            if (!a.path.StartsWith("Assets/")) return Response.Fail("path 는 Assets/ 로 시작해야 함");

            if (_sessionRoot != null)
                CancelPrefab();

            EnsureFolder(Path.GetDirectoryName(a.path).Replace('\\', '/'));

            var rootName = Path.GetFileNameWithoutExtension(a.path);
            var go = new GameObject(rootName, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var saved = PrefabUtility.SaveAsPrefabAsset(go, a.path, out bool ok);
            UnityEngine.Object.DestroyImmediate(go);
            if (!ok || saved == null) return Response.Fail("프리팹 저장 실패");

            _sessionRoot = PrefabUtility.LoadPrefabContents(a.path);
            _sessionPath = a.path;
            return Response.Ok(new { path = a.path });
        }

        private static Response OpenPrefab(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a?.path)) return Response.Fail("path 필요");
            if (_sessionRoot != null) return Response.Fail("이미 세션이 열려있음 (save/cancel 먼저)");
            if (!File.Exists(a.path)) return Response.Fail("프리팹 없음: " + a.path);

            _sessionRoot = PrefabUtility.LoadPrefabContents(a.path);
            _sessionPath = a.path;
            return Response.Ok(new { path = a.path });
        }

        private static Response SavePrefab()
        {
            if (_sessionRoot == null) return Response.Fail("세션 없음");
            var path = _sessionPath;
            PrefabUtility.SaveAsPrefabAsset(_sessionRoot, path, out bool ok);
            PrefabUtility.UnloadPrefabContents(_sessionRoot);
            _sessionRoot = null;
            _sessionPath = null;
            if (!ok) return Response.Fail("저장 실패");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Ok(new { path });
        }

        private static Response CancelPrefab()
        {
            if (_sessionRoot == null) return Response.Ok(new { cancelled = false });
            PrefabUtility.UnloadPrefabContents(_sessionRoot);
            _sessionRoot = null;
            _sessionPath = null;
            return Response.Ok(new { cancelled = true });
        }

        #endregion

        #region Hierarchy

        private static Response AddChild(string argsJson)
        {
            var a = JsonUtility.FromJson<ChildArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a?.name)) return Response.Fail("name 필요");
            var parent = ResolveTransform(a.parent);
            if (parent == null) return Response.Fail("parent 못 찾음: " + a.parent);

            var go = new GameObject(a.name, typeof(RectTransform));
            go.transform.SetParent(parent, worldPositionStays: false);
            return Response.Ok(new { path = GetPath(go.transform) });
        }

        private static Response AddNestedPrefab(string argsJson)
        {
            var a = JsonUtility.FromJson<NestedArgs>(argsJson ?? "{}");
            if (string.IsNullOrEmpty(a?.prefabPath)) return Response.Fail("prefabPath 필요");
            var parent = ResolveTransform(a.parent);
            if (parent == null) return Response.Fail("parent 못 찾음: " + a.parent);

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(a.prefabPath);
            if (asset == null) return Response.Fail("프리팹 에셋 없음: " + a.prefabPath);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            if (!string.IsNullOrEmpty(a.name)) instance.name = a.name;
            return Response.Ok(new { path = GetPath(instance.transform) });
        }

        private static Response Delete(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            if (t == _sessionRoot?.transform) return Response.Fail("루트는 삭제 불가");
            UnityEngine.Object.DestroyImmediate(t.gameObject);
            return Response.Ok(new { deleted = true });
        }

        [Serializable] private class RenameArgs { public string path; public string newName; }
        private static Response Rename(string argsJson)
        {
            var a = JsonUtility.FromJson<RenameArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            if (string.IsNullOrEmpty(a.newName)) return Response.Fail("newName 필요");
            t.gameObject.name = a.newName;
            return Response.Ok(new { path = GetPath(t) });
        }

        [Serializable] private class ReparentArgs { public string path; public string newParent; public int index; public bool hasIndex; }
        private static Response Reparent(string argsJson)
        {
            var a = JsonUtility.FromJson<ReparentArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var p = ResolveTransform(a.newParent);
            if (p == null) return Response.Fail("newParent 못 찾음");
            t.SetParent(p, worldPositionStays: false);
            if (a.hasIndex) t.SetSiblingIndex(a.index);
            return Response.Ok(new { path = GetPath(t) });
        }

        [Serializable] private class SiblingArgs { public string path; public int index; }
        private static Response SetSiblingIndex(string argsJson)
        {
            var a = JsonUtility.FromJson<SiblingArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            t.SetSiblingIndex(a.index);
            return Response.Ok(new { index = t.GetSiblingIndex() });
        }

        [Serializable] private class ActiveArgs { public string path; public bool active; }
        private static Response SetActive(string argsJson)
        {
            var a = JsonUtility.FromJson<ActiveArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            t.gameObject.SetActive(a.active);
            return Response.Ok(new { active = a.active });
        }

        private static Response Duplicate(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            if (t == _sessionRoot?.transform) return Response.Fail("루트는 복제 불가");
            var copy = UnityEngine.Object.Instantiate(t.gameObject, t.parent);
            copy.name = t.gameObject.name;
            return Response.Ok(new { path = GetPath(copy.transform) });
        }

        #endregion

        #region RectTransform

        [Serializable]
        private class RectArgs
        {
            public string path;
            public string anchor;    // preset 이름
            public float x, y, w, h;
            public bool hasPos;      // x,y 사용 여부
            public bool hasSize;     // w,h 사용 여부
            public float pivotX = 0.5f, pivotY = 0.5f;
            public bool hasPivot;
        }

        private static Response SetRect(string argsJson)
        {
            var a = JsonUtility.FromJson<RectArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음: " + a?.path);

            if (!string.IsNullOrEmpty(a.anchor))
            {
                if (!ApplyAnchorPreset(rt, a.anchor, out var preset))
                    return Response.Fail("unknown anchor: " + a.anchor);
                if (a.hasPivot) rt.pivot = new Vector2(a.pivotX, a.pivotY);
                else            rt.pivot = preset.pivot;
            }
            else if (a.hasPivot)
            {
                rt.pivot = new Vector2(a.pivotX, a.pivotY);
            }

            var isStretch = a.anchor == "stretch";
            if (isStretch)
            {
                if (a.hasPos && a.hasSize)
                {
                    rt.offsetMin = new Vector2(a.x, a.y);
                    rt.offsetMax = new Vector2(-a.w, -a.h);
                }
                else
                {
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }
            else
            {
                if (a.hasPos) rt.anchoredPosition = new Vector2(a.x, a.y);
                if (a.hasSize) rt.sizeDelta = new Vector2(a.w, a.h);
            }

            return Response.Ok(new { path = GetPath(rt) });
        }

        [Serializable] private class AnchorArgs { public string path; public float minX, minY, maxX, maxY; }
        private static Response SetAnchor(string argsJson)
        {
            var a = JsonUtility.FromJson<AnchorArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음");
            rt.anchorMin = new Vector2(a.minX, a.minY);
            rt.anchorMax = new Vector2(a.maxX, a.maxY);
            return Response.Ok(new { });
        }

        [Serializable] private class PivotArgs { public string path; public float x, y; }
        private static Response SetPivot(string argsJson)
        {
            var a = JsonUtility.FromJson<PivotArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음");
            rt.pivot = new Vector2(a.x, a.y);
            return Response.Ok(new { });
        }

        [Serializable] private class RotArgs { public string path; public float z; }
        private static Response SetRotation(string argsJson)
        {
            var a = JsonUtility.FromJson<RotArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음");
            rt.localEulerAngles = new Vector3(0, 0, a.z);
            return Response.Ok(new { });
        }

        [Serializable] private class ScaleArgs { public string path; public float x = 1f, y = 1f; }
        private static Response SetScale(string argsJson)
        {
            var a = JsonUtility.FromJson<ScaleArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음");
            rt.localScale = new Vector3(a.x, a.y, 1f);
            return Response.Ok(new { });
        }

        #endregion

        #region Graphics

        [Serializable]
        private class ImageArgs
        {
            public string path;
            public string sprite;   // AssetDatabase 경로
            public string color;    // #RRGGBB 또는 #RRGGBBAA
            public string type;     // Simple / Sliced / Tiled / Filled
            public bool raycastTarget = true;
            public bool hasRaycast;
        }

        private static Response AddImage(string argsJson)
        {
            var a = JsonUtility.FromJson<ImageArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var img = t.gameObject.GetComponent<Image>() ?? t.gameObject.AddComponent<Image>();
            ApplyImageArgs(img, a);
            return Response.Ok(new { });
        }

        private static void ApplyImageArgs(Image img, ImageArgs a)
        {
            if (!string.IsNullOrEmpty(a.sprite))
            {
                var s = AssetDatabase.LoadAssetAtPath<Sprite>(a.sprite);
                if (s != null) img.sprite = s;
            }
            if (TryParseColor(a.color, out var c)) img.color = c;
            if (Enum.TryParse<Image.Type>(a.type, ignoreCase: true, out var it)) img.type = it;
            if (a.hasRaycast) img.raycastTarget = a.raycastTarget;
        }

        [Serializable]
        private class RawImageArgs { public string path; public string texture; public string color; }
        private static Response AddRawImage(string argsJson)
        {
            var a = JsonUtility.FromJson<RawImageArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var ri = t.gameObject.GetComponent<RawImage>() ?? t.gameObject.AddComponent<RawImage>();
            if (!string.IsNullOrEmpty(a.texture))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(a.texture);
                if (tex != null) ri.texture = tex;
            }
            if (TryParseColor(a.color, out var c)) ri.color = c;
            return Response.Ok(new { });
        }

        [Serializable]
        private class TmpArgs
        {
            public string path;
            public string text;
            public float fontSize;
            public bool hasFontSize;
            public string color;
            public string alignment;  // Center / TopLeft / MidCenter / ...
            public string font;       // font asset path
        }

        private static Response AddTmp(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var tmp = t.gameObject.GetComponent<TextMeshProUGUI>() ?? t.gameObject.AddComponent<TextMeshProUGUI>();
            if (a.text != null) tmp.text = a.text;
            if (a.hasFontSize) tmp.fontSize = a.fontSize;
            if (TryParseColor(a.color, out var c)) tmp.color = c;
            if (Enum.TryParse<TextAlignmentOptions>(a.alignment, ignoreCase: true, out var al)) tmp.alignment = al;
            if (!string.IsNullOrEmpty(a.font))
            {
                var f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(a.font);
                if (f != null) tmp.font = f;
            }
            return Response.Ok(new { });
        }

        private static Response SetImageSprite(string argsJson)
        {
            var a = JsonUtility.FromJson<ImageArgs>(argsJson ?? "{}");
            var img = GetComponentOrNull<Image>(a?.path);
            if (img == null) return Response.Fail("Image 없음: " + a?.path);
            if (!string.IsNullOrEmpty(a.sprite))
            {
                var s = AssetDatabase.LoadAssetAtPath<Sprite>(a.sprite);
                if (s == null) return Response.Fail("스프라이트 에셋 없음: " + a.sprite);
                img.sprite = s;
            }
            return Response.Ok(new { });
        }

        private static Response SetImageColor(string argsJson)
        {
            var a = JsonUtility.FromJson<ImageArgs>(argsJson ?? "{}");
            var img = GetComponentOrNull<Image>(a?.path);
            if (img == null) return Response.Fail("Image 없음");
            if (!TryParseColor(a.color, out var c)) return Response.Fail("color 파싱 실패");
            img.color = c;
            return Response.Ok(new { });
        }

        private static Response SetImageType(string argsJson)
        {
            var a = JsonUtility.FromJson<ImageArgs>(argsJson ?? "{}");
            var img = GetComponentOrNull<Image>(a?.path);
            if (img == null) return Response.Fail("Image 없음");
            if (!Enum.TryParse<Image.Type>(a.type, ignoreCase: true, out var it)) return Response.Fail("type 파싱 실패");
            img.type = it;
            return Response.Ok(new { });
        }

        private static Response SetTmpText(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var tmp = GetComponentOrNull<TextMeshProUGUI>(a?.path);
            if (tmp == null) return Response.Fail("TMP 없음");
            tmp.text = a.text ?? "";
            return Response.Ok(new { });
        }

        private static Response SetTmpFontSize(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var tmp = GetComponentOrNull<TextMeshProUGUI>(a?.path);
            if (tmp == null) return Response.Fail("TMP 없음");
            tmp.fontSize = a.fontSize;
            return Response.Ok(new { });
        }

        private static Response SetTmpColor(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var tmp = GetComponentOrNull<TextMeshProUGUI>(a?.path);
            if (tmp == null) return Response.Fail("TMP 없음");
            if (!TryParseColor(a.color, out var c)) return Response.Fail("color 파싱 실패");
            tmp.color = c;
            return Response.Ok(new { });
        }

        private static Response SetTmpAlignment(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var tmp = GetComponentOrNull<TextMeshProUGUI>(a?.path);
            if (tmp == null) return Response.Fail("TMP 없음");
            if (!Enum.TryParse<TextAlignmentOptions>(a.alignment, ignoreCase: true, out var al))
                return Response.Fail("alignment 파싱 실패");
            tmp.alignment = al;
            return Response.Ok(new { });
        }

        private static Response SetTmpFont(string argsJson)
        {
            var a = JsonUtility.FromJson<TmpArgs>(argsJson ?? "{}");
            var tmp = GetComponentOrNull<TextMeshProUGUI>(a?.path);
            if (tmp == null) return Response.Fail("TMP 없음");
            var f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(a.font);
            if (f == null) return Response.Fail("font 에셋 없음: " + a.font);
            tmp.font = f;
            return Response.Ok(new { });
        }

        #endregion

        #region Interactables

        [Serializable] private class ScrollbarArgs { public string path; public string direction; }
        private static Response AddScrollbar(string argsJson)
        {
            var a = JsonUtility.FromJson<ScrollbarArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var sb = t.gameObject.GetComponent<Scrollbar>() ?? t.gameObject.AddComponent<Scrollbar>();
            if (Enum.TryParse<Scrollbar.Direction>(a.direction, ignoreCase: true, out var d))
                sb.direction = d;
            return Response.Ok(new { });
        }

        [Serializable] private class ToggleArgs { public string path; public bool isOn = false; }
        private static Response AddToggle(string argsJson)
        {
            var a = JsonUtility.FromJson<ToggleArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var tg = t.gameObject.GetComponent<Toggle>() ?? t.gameObject.AddComponent<Toggle>();
            tg.isOn = a.isOn;
            return Response.Ok(new { });
        }

        [Serializable]
        private class ScrollRectArgs
        {
            public string path;
            public bool horizontal = false;
            public bool vertical = true;
            public bool hasHorizontal;
            public bool hasVertical;
            public string viewport;   // 자식 상대경로 또는 세션 기준 절대경로
            public string content;
            public string movementType;   // Unrestricted / Elastic / Clamped
            public float elasticity = 0.1f;
            public bool hasElasticity;
        }

        /// <summary>
        /// ScrollRect 부착 + viewport/content 자동 배선.
        /// viewport 인자 미지정 시 자식에서 "Viewport" 이름 GameObject 자동 탐색,
        /// content 인자 미지정 시 viewport 하위 "Content" 자동 탐색
        /// </summary>
        private static Response AddScrollRect(string argsJson)
        {
            var a = JsonUtility.FromJson<ScrollRectArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음: " + a?.path);

            var sr = t.gameObject.GetComponent<ScrollRect>() ?? t.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = a.hasHorizontal ? a.horizontal : false;
            sr.vertical = a.hasVertical ? a.vertical : true;
            if (Enum.TryParse<ScrollRect.MovementType>(a.movementType, ignoreCase: true, out var mt))
                sr.movementType = mt;
            if (a.hasElasticity) sr.elasticity = a.elasticity;

            var viewport = ResolveChildPreferExplicit(t, a.viewport, "Viewport") as RectTransform;
            if (viewport != null)
            {
                sr.viewport = viewport;
                // 마스크·이미지 보장 — 스크롤 영역 밖을 자름
                if (viewport.GetComponent<RectMask2D>() == null && viewport.GetComponent<Mask>() == null)
                {
                    if (viewport.GetComponent<Graphic>() == null)
                        viewport.gameObject.AddComponent<Image>().color = new Color(1, 1, 1, 0);
                    viewport.gameObject.AddComponent<RectMask2D>();
                }
            }

            var contentStart = viewport ?? t;
            var content = ResolveChildPreferExplicit(contentStart, a.content, "Content") as RectTransform;
            if (content != null) sr.content = content;

            return Response.Ok(new ScrollRectResult
            {
                viewport = viewport != null ? GetPath(viewport) : "",
                content = content != null ? GetPath(content) : "",
            });
        }

        [Serializable] private class ScrollRectResult { public string viewport; public string content; }

        private static Transform ResolveChildPreferExplicit(Transform root, string explicitPath, string defaultChildName)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                // 세션 루트 기준 절대 경로 우선
                var abs = ResolveTransform(explicitPath);
                if (abs != null) return abs;
                // root 상대 탐색
                var rel = root.Find(explicitPath);
                if (rel != null) return rel;
            }
            return root.Find(defaultChildName);
        }

        #endregion

        #region Layout

        [Serializable]
        private class LayoutGroupArgs
        {
            public string path;
            public float spacing;
            public float padLeft, padRight, padTop, padBottom;
            public string childAlignment;
            public bool childForceExpandWidth = true, childForceExpandHeight = true;
            public bool hasForceExpand;
            public bool childControlWidth = true, childControlHeight = true;
            public bool hasControl;
        }

        private static Response AddHorizontalLayout(string argsJson)
        {
            var a = JsonUtility.FromJson<LayoutGroupArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var g = t.gameObject.GetComponent<HorizontalLayoutGroup>() ?? t.gameObject.AddComponent<HorizontalLayoutGroup>();
            // 가로 스택 기본값: 자식 width 는 LayoutElement 가 결정, height 는 부모에 맞춤
            ApplyLayoutGroup(g, a,
                defaultControlWidth: true, defaultControlHeight: true,
                defaultForceExpandWidth: false, defaultForceExpandHeight: true);
            return Response.Ok(new { });
        }

        private static Response AddVerticalLayout(string argsJson)
        {
            var a = JsonUtility.FromJson<LayoutGroupArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var g = t.gameObject.GetComponent<VerticalLayoutGroup>() ?? t.gameObject.AddComponent<VerticalLayoutGroup>();
            // 세로 스택 기본값: 자식 width 는 부모에 맞춤, height 는 LayoutElement 가 결정
            ApplyLayoutGroup(g, a,
                defaultControlWidth: true, defaultControlHeight: true,
                defaultForceExpandWidth: true, defaultForceExpandHeight: false);
            return Response.Ok(new { });
        }

        private static void ApplyLayoutGroup(
            HorizontalOrVerticalLayoutGroup g, LayoutGroupArgs a,
            bool defaultControlWidth, bool defaultControlHeight,
            bool defaultForceExpandWidth, bool defaultForceExpandHeight)
        {
            g.spacing = a.spacing;
            g.padding = new RectOffset((int)a.padLeft, (int)a.padRight, (int)a.padTop, (int)a.padBottom);
            if (Enum.TryParse<TextAnchor>(a.childAlignment, ignoreCase: true, out var ta)) g.childAlignment = ta;

            g.childControlWidth       = a.hasControl      ? a.childControlWidth       : defaultControlWidth;
            g.childControlHeight      = a.hasControl      ? a.childControlHeight      : defaultControlHeight;
            g.childForceExpandWidth   = a.hasForceExpand  ? a.childForceExpandWidth   : defaultForceExpandWidth;
            g.childForceExpandHeight  = a.hasForceExpand  ? a.childForceExpandHeight  : defaultForceExpandHeight;
        }

        [Serializable]
        private class GridLayoutArgs
        {
            public string path;
            public float cellW = 100, cellH = 100;
            public float spacingX, spacingY;
            public float padLeft, padRight, padTop, padBottom;
            public int constraintCount;
            public string constraint;
            public string startAxis;
            public string childAlignment;
        }

        private static Response AddGridLayout(string argsJson)
        {
            var a = JsonUtility.FromJson<GridLayoutArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var g = t.gameObject.GetComponent<GridLayoutGroup>() ?? t.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(a.cellW, a.cellH);
            g.spacing = new Vector2(a.spacingX, a.spacingY);
            g.padding = new RectOffset((int)a.padLeft, (int)a.padRight, (int)a.padTop, (int)a.padBottom);
            if (Enum.TryParse<GridLayoutGroup.Constraint>(a.constraint, ignoreCase: true, out var c)) g.constraint = c;
            if (a.constraintCount > 0) g.constraintCount = a.constraintCount;
            if (Enum.TryParse<GridLayoutGroup.Axis>(a.startAxis, ignoreCase: true, out var ax)) g.startAxis = ax;
            if (Enum.TryParse<TextAnchor>(a.childAlignment, ignoreCase: true, out var ca)) g.childAlignment = ca;
            return Response.Ok(new { });
        }

        [Serializable]
        private class LayoutElementArgs
        {
            public string path;
            public float minWidth = -1, minHeight = -1;
            public float preferredWidth = -1, preferredHeight = -1;
            public float flexibleWidth = -1, flexibleHeight = -1;
            public bool ignoreLayout;
        }

        private static Response AddLayoutElement(string argsJson)
        {
            var a = JsonUtility.FromJson<LayoutElementArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var le = t.gameObject.GetComponent<LayoutElement>() ?? t.gameObject.AddComponent<LayoutElement>();
            le.minWidth = a.minWidth;
            le.minHeight = a.minHeight;
            le.preferredWidth = a.preferredWidth;
            le.preferredHeight = a.preferredHeight;
            le.flexibleWidth = a.flexibleWidth;
            le.flexibleHeight = a.flexibleHeight;
            le.ignoreLayout = a.ignoreLayout;
            return Response.Ok(new { });
        }

        [Serializable] private class ContentSizeFitterArgs { public string path; public string horizontalFit; public string verticalFit; }
        private static Response AddContentSizeFitter(string argsJson)
        {
            var a = JsonUtility.FromJson<ContentSizeFitterArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var f = t.gameObject.GetComponent<ContentSizeFitter>() ?? t.gameObject.AddComponent<ContentSizeFitter>();
            if (Enum.TryParse<ContentSizeFitter.FitMode>(a.horizontalFit, ignoreCase: true, out var h)) f.horizontalFit = h;
            if (Enum.TryParse<ContentSizeFitter.FitMode>(a.verticalFit, ignoreCase: true, out var v)) f.verticalFit = v;
            return Response.Ok(new { });
        }

        [Serializable] private class AspectFitterArgs { public string path; public string aspectMode; public float aspectRatio = 1f; }
        private static Response AddAspectRatioFitter(string argsJson)
        {
            var a = JsonUtility.FromJson<AspectFitterArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var f = t.gameObject.GetComponent<AspectRatioFitter>() ?? t.gameObject.AddComponent<AspectRatioFitter>();
            if (Enum.TryParse<AspectRatioFitter.AspectMode>(a.aspectMode, ignoreCase: true, out var m)) f.aspectMode = m;
            f.aspectRatio = a.aspectRatio;
            return Response.Ok(new { });
        }

        #endregion

        #region Canvas / Mask / Effect

        [Serializable] private class SubCanvasArgs { public string path; public bool overrideSorting; public int sortingOrder; }
        private static Response AddSubCanvas(string argsJson)
        {
            var a = JsonUtility.FromJson<SubCanvasArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var c = t.gameObject.GetComponent<Canvas>() ?? t.gameObject.AddComponent<Canvas>();
            c.overrideSorting = a.overrideSorting;
            c.sortingOrder = a.sortingOrder;
            if (t.gameObject.GetComponent<GraphicRaycaster>() == null)
                t.gameObject.AddComponent<GraphicRaycaster>();
            return Response.Ok(new { });
        }

        [Serializable] private class CanvasGroupArgs { public string path; public float alpha = 1f; public bool interactable = true; public bool blocksRaycasts = true; public bool ignoreParentGroups; }
        private static Response AddCanvasGroup(string argsJson)
        {
            var a = JsonUtility.FromJson<CanvasGroupArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var cg = t.gameObject.GetComponent<CanvasGroup>() ?? t.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = a.alpha;
            cg.interactable = a.interactable;
            cg.blocksRaycasts = a.blocksRaycasts;
            cg.ignoreParentGroups = a.ignoreParentGroups;
            return Response.Ok(new { });
        }

        [Serializable] private class MaskArgs { public string path; public bool showMaskGraphic = true; }
        private static Response AddMask(string argsJson)
        {
            var a = JsonUtility.FromJson<MaskArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            if (t.gameObject.GetComponent<Graphic>() == null) t.gameObject.AddComponent<Image>();
            var m = t.gameObject.GetComponent<Mask>() ?? t.gameObject.AddComponent<Mask>();
            m.showMaskGraphic = a.showMaskGraphic;
            return Response.Ok(new { });
        }

        [Serializable] private class ShadowArgs { public string path; public string color; public float x = 1f, y = -1f; }
        private static Response AddShadow(string argsJson)
        {
            var a = JsonUtility.FromJson<ShadowArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var s = t.gameObject.GetComponent<Shadow>() ?? t.gameObject.AddComponent<Shadow>();
            if (TryParseColor(a.color, out var c)) s.effectColor = c;
            s.effectDistance = new Vector2(a.x, a.y);
            return Response.Ok(new { });
        }

        private static Response AddOutline(string argsJson)
        {
            var a = JsonUtility.FromJson<ShadowArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var o = t.gameObject.GetComponent<Outline>() ?? t.gameObject.AddComponent<Outline>();
            if (TryParseColor(a.color, out var c)) o.effectColor = c;
            o.effectDistance = new Vector2(a.x, a.y);
            return Response.Ok(new { });
        }

        #endregion

        #region Generic / Introspection

        [Serializable] private class TypeNameArgs { public string path; public string typeName; }
        private static Response AddComponentByName(string argsJson)
        {
            var a = JsonUtility.FromJson<TypeNameArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var type = ResolveType(a.typeName);
            if (type == null) return Response.Fail("타입 못 찾음: " + a.typeName);
            if (!typeof(Component).IsAssignableFrom(type)) return Response.Fail("Component 파생 아님: " + a.typeName);
            t.gameObject.AddComponent(type);
            return Response.Ok(new { typeName = type.FullName });
        }

        private static Response ListChildren(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            var names = new List<string>();
            for (int i = 0; i < t.childCount; i++) names.Add(t.GetChild(i).gameObject.name);
            return Response.Ok(new ChildrenList { children = names.ToArray() });
        }

        [Serializable] private class ChildrenList { public string[] children; }

        private static Response GetRect(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var rt = ResolveRect(a?.path);
            if (rt == null) return Response.Fail("RectTransform 못 찾음");
            return Response.Ok(new RectInfo
            {
                anchorMinX = rt.anchorMin.x, anchorMinY = rt.anchorMin.y,
                anchorMaxX = rt.anchorMax.x, anchorMaxY = rt.anchorMax.y,
                pivotX = rt.pivot.x, pivotY = rt.pivot.y,
                posX = rt.anchoredPosition.x, posY = rt.anchoredPosition.y,
                sizeW = rt.sizeDelta.x, sizeH = rt.sizeDelta.y,
            });
        }

        [Serializable]
        private class RectInfo
        {
            public float anchorMinX, anchorMinY, anchorMaxX, anchorMaxY;
            public float pivotX, pivotY;
            public float posX, posY;
            public float sizeW, sizeH;
        }

        private static Response Exists(string argsJson)
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            return Response.Ok(new ExistsResult { exists = t != null });
        }

        [Serializable] private class ExistsResult { public bool exists; }

        #endregion

        #region Generic component helper

        private static Response AddComponent<T>(string argsJson, bool ensureGraphic = false) where T : Component
        {
            var a = JsonUtility.FromJson<PathArgs>(argsJson ?? "{}");
            var t = ResolveTransform(a?.path);
            if (t == null) return Response.Fail("path 못 찾음");
            if (ensureGraphic && t.gameObject.GetComponent<Graphic>() == null)
                t.gameObject.AddComponent<Image>();
            if (t.gameObject.GetComponent<T>() == null)
                t.gameObject.AddComponent<T>();
            return Response.Ok(new { });
        }

        #endregion

        #region Helpers

        private static Transform ResolveTransform(string path)
        {
            if (_sessionRoot == null) return null;
            if (string.IsNullOrEmpty(path) || path == "/" || path == ".")
                return _sessionRoot.transform;
            var rootName = _sessionRoot.name;
            var p = path.StartsWith(rootName + "/") ? path.Substring(rootName.Length + 1) : path;
            return _sessionRoot.transform.Find(p);
        }

        private static RectTransform ResolveRect(string path)
        {
            var t = ResolveTransform(path);
            return t as RectTransform;
        }

        private static T GetComponentOrNull<T>(string path) where T : Component
        {
            var t = ResolveTransform(path);
            return t == null ? null : t.GetComponent<T>();
        }

        private static string GetPath(Transform t)
        {
            if (t == null || _sessionRoot == null) return "";
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != _sessionRoot.transform)
            {
                parts.Add(cur.gameObject.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            var leaf = Path.GetFileName(folder);
            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        private static bool TryParseColor(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            return ColorUtility.TryParseHtmlString(hex, out color);
        }

        private static Type ResolveType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name, throwOnError: false);
                if (t != null) return t;
                foreach (var candidate in asm.GetTypes())
                {
                    if (candidate.Name == name) return candidate;
                }
            }
            return null;
        }

        private static bool ApplyAnchorPreset(RectTransform rt, string preset, out AnchorPresetValues v)
        {
            v = default;
            switch (preset.ToLowerInvariant())
            {
                case "top-left":      v = new AnchorPresetValues(new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1)); break;
                case "top-center":    v = new AnchorPresetValues(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1)); break;
                case "top-right":     v = new AnchorPresetValues(new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1)); break;
                case "middle-left":   v = new AnchorPresetValues(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f)); break;
                case "center":
                case "middle-center": v = new AnchorPresetValues(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)); break;
                case "middle-right":  v = new AnchorPresetValues(new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f)); break;
                case "bottom-left":   v = new AnchorPresetValues(new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0)); break;
                case "bottom-center": v = new AnchorPresetValues(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0)); break;
                case "bottom-right":  v = new AnchorPresetValues(new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0)); break;
                case "stretch":
                case "stretch-all":   v = new AnchorPresetValues(new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f)); break;
                case "stretch-top":   v = new AnchorPresetValues(new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1)); break;
                case "stretch-bottom":v = new AnchorPresetValues(new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0)); break;
                case "stretch-left":  v = new AnchorPresetValues(new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f)); break;
                case "stretch-right": v = new AnchorPresetValues(new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f)); break;
                default: return false;
            }
            rt.anchorMin = v.anchorMin;
            rt.anchorMax = v.anchorMax;
            return true;
        }

        private struct AnchorPresetValues
        {
            public Vector2 anchorMin, anchorMax, pivot;
            public AnchorPresetValues(Vector2 min, Vector2 max, Vector2 p) { anchorMin = min; anchorMax = max; pivot = p; }
        }

        #endregion

        [Serializable]
        private class Command { public string op; public string args; }

        [Serializable]
        private class Response
        {
            public bool success;
            public string error;
            public string data;   // JsonUtility.ToJson 결과를 문자열로 담음

            public static Response Ok(object payload)
            {
                string dataJson;
                try { dataJson = payload == null ? "{}" : JsonUtility.ToJson(payload); }
                catch { dataJson = "{}"; }
                return new Response { success = true, data = dataJson };
            }

            public static Response Fail(string msg) => new Response { success = false, error = msg, data = "{}" };
        }
    }
}
