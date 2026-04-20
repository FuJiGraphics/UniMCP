// JsonUtility 가 채우는 필드는 컴파일러가 미할당으로 오진
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UniMCP.Editor.Logging;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// TreeSpec JSON 을 받아 Unity API 로 프리팹을 결정론적으로 생성한다.
    /// Analyzer (Claude) 가 구조만 추론하고, 실제 GameObject 트리 구축은 규칙대로 여기가 수행.
    /// 훅 IPC 거치지 않고 Editor API 를 직접 호출해 빠르고 안정적
    /// </summary>
    public static class TreeGenerator
    {
        [Serializable]
        public class TreeSpec
        {
            public string outputPath;
            public float[] size;   // [w, h]
            public Node root;
        }

        [Serializable]
        public class Node
        {
            public string name;

            public bool box;
            public string color;
            public string outlineColor;
            public string sprite;             // Image 에 적용할 스프라이트 에셋 경로 (Asset path)

            public string layout;             // "vertical" | "horizontal" | ""
            public float spacing;
            public float[] padding;           // [L, R, T, B]

            public float preferredWidth = -1;
            public float preferredHeight = -1;
            public float flexibleWidth = -1;
            public float flexibleHeight = -1;

            public string text;
            public string textAlign;
            public float fontSize;
            public string textColor;

            public bool button;

            public bool scroll;
            public string scrollDirection;    // "vertical" | "horizontal"
            public string scrollLayout;       // "vertical" | "horizontal" | "grid" (Content 내부 아이템 배치)
            public float scrollCellWidth = 200;
            public float scrollCellHeight = 200;

            public string fontPath;           // TMP Font Asset 경로 (manifest.referenceFont 기본)
            public string nestedPrefab;       // 기존 프리팹 인스턴스 재사용 (이 필드 지정되면 box/button/text/layout 무시)

            // 오버레이(복잡 레이아웃): 부모의 LayoutGroup 을 무시하고 anchor + offset 으로 절대 배치.
            // 배경·전경 레이어·팝업 중앙 배치·절대 위치 요소 표현용
            public bool overlay;
            public string anchor;             // topLeft|top|topRight|left|center|right|bottomLeft|bottom|bottomRight|stretch
            public float offsetX;
            public float offsetY;

            // 프로그레스 바 (게이지). Image.type = Filled 로 부채꼴/선형 fill 지원
            public bool progressBar;
            public string fillDirection;      // vertical | horizontal (기본 vertical)
            public float fillAmount = 0.5f;   // 0~1
            public string fillColor;          // #RRGGBBAA, 기본 주황

            public Node[] children;
        }

        public class Result
        {
            public bool success;
            public string error;
            public string prefabPath;
        }

        /// <summary>
        /// TreeSpec JSON 을 받아 프리팹 생성.
        /// 세션 사용 안 함 (독립적으로 완결)
        /// </summary>
        public static Result Generate(string treeJson)
        {
            TreeSpec spec;
            try { spec = JsonUtility.FromJson<TreeSpec>(treeJson); }
            catch (Exception e) { return Fail("TreeSpec JSON 파싱 실패: " + e.Message); }

            if (spec == null) return Fail("TreeSpec null");
            if (string.IsNullOrEmpty(spec.outputPath)) return Fail("outputPath 필수");
            if (!spec.outputPath.StartsWith("Assets/")) return Fail("outputPath 는 Assets/ 아래여야 함");
            if (spec.root == null) return Fail("root 노드 필수");

            EnsureFolder(Path.GetDirectoryName(spec.outputPath).Replace('\\', '/'));

            var rootName = string.IsNullOrEmpty(spec.root.name)
                ? Path.GetFileNameWithoutExtension(spec.outputPath)
                : spec.root.name;

            var rootGo = new GameObject(rootName, typeof(RectTransform));
            try
            {
                var rootRt = (RectTransform)rootGo.transform;
                rootRt.anchorMin = new Vector2(0.5f, 0.5f);
                rootRt.anchorMax = new Vector2(0.5f, 0.5f);
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.anchoredPosition = Vector2.zero;

                var size = (spec.size != null && spec.size.Length >= 2)
                    ? new Vector2(spec.size[0], spec.size[1])
                    : new Vector2(1080, 1920);
                rootRt.sizeDelta = size;

                ApplyNode(rootGo, spec.root, isRoot: true);

                var saved = PrefabUtility.SaveAsPrefabAsset(rootGo, spec.outputPath, out bool ok);
                if (!ok || saved == null) return Fail("SaveAsPrefabAsset 실패");

                AssetDatabase.ImportAsset(spec.outputPath, ImportAssetOptions.ForceUpdate);
                return new Result { success = true, prefabPath = spec.outputPath };
            }
            catch (Exception e)
            {
                UniMcpLogger.Warn("TreeGenerator 예외: " + e.Message + "\n" + e.StackTrace);
                return Fail(e.Message);
            }
            finally
            {
                if (rootGo != null) UnityEngine.Object.DestroyImmediate(rootGo);
            }
        }

        /// <summary>
        /// 노드의 속성(box/layout/text/button/scroll)을 GameObject 에 적용하고 자식을 재귀 생성.
        /// 컨벤션: 레이아웃은 **별도 자식 GameObject** (VLayout/HLayout/GLayout)에 부착. 박스(Image+Outline) 에 직접 레이아웃 컴포넌트 붙이지 않는다
        /// </summary>
        private static void ApplyNode(GameObject go, Node node, bool isRoot = false)
        {
            if (node == null) return;

            // nestedPrefab: 기존 프리팹을 인스턴스화해 go 를 대체. box/button/text/layout 모두 무시
            if (!string.IsNullOrEmpty(node.nestedPrefab))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(node.nestedPrefab);
                if (asset != null)
                {
                    var parent = go.transform.parent;
                    var siblingIdx = go.transform.GetSiblingIndex();
                    var savedName = go.name;

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
                    instance.name = savedName;
                    instance.transform.SetSiblingIndex(siblingIdx);

                    // LayoutElement 복사 (부모 레이아웃 내 크기)
                    if (!isRoot && HasAnyLayoutElementField(node))
                    {
                        var le = instance.GetComponent<LayoutElement>() ?? instance.AddComponent<LayoutElement>();
                        if (node.preferredWidth  >= 0) le.preferredWidth  = node.preferredWidth;
                        if (node.preferredHeight >= 0) le.preferredHeight = node.preferredHeight;
                        if (node.flexibleWidth   >= 0) le.flexibleWidth   = node.flexibleWidth;
                        if (node.flexibleHeight  >= 0) le.flexibleHeight  = node.flexibleHeight;
                    }

                    UnityEngine.Object.DestroyImmediate(go);
                    return;
                }
                // 프리팹 로드 실패 시 fallback 으로 일반 box 처리 계속
                UniMcpLogger.Warn("nestedPrefab 로드 실패 → box 로 fallback: " + node.nestedPrefab);
            }

            // Progress Bar (게이지): Image.type = Filled. box 와 배타적
            if (node.progressBar)
            {
                BuildProgressBar(go, node);
            }

            // Box 시각 (Image + Outline) — go 자체에 부착
            if (node.box && !node.progressBar)
            {
                var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
                img.color = ParseColor(node.color, defaultValue: new Color(1, 1, 1, 1));

                if (!string.IsNullOrEmpty(node.sprite))
                {
                    var sp = AssetDatabase.LoadAssetAtPath<Sprite>(node.sprite);
                    if (sp != null) img.sprite = sp;
                }

                var outlineCol = ParseColor(node.outlineColor, defaultValue: new Color(0.07f, 0.07f, 0.07f, 1));
                var outline = go.GetComponent<Outline>() ?? go.AddComponent<Outline>();
                outline.effectColor = outlineCol;
                outline.effectDistance = new Vector2(2, -2);
            }

            // Button
            if (node.button)
            {
                if (go.GetComponent<Graphic>() == null)
                {
                    var img = go.AddComponent<Image>();
                    img.color = ParseColor(node.color, defaultValue: new Color(1, 1, 1, 1));
                }
                if (go.GetComponent<Button>() == null)
                    go.AddComponent<Button>();
            }

            // Overlay: 부모 LayoutGroup 무시하고 anchor+offset 으로 절대 배치
            if (!isRoot && node.overlay)
            {
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                ApplyOverlayRect((RectTransform)go.transform, node);
            }
            // 일반 LayoutElement (부모 레이아웃 내 이 노드 크기) — 루트·overlay 는 스킵
            else if (!isRoot && HasAnyLayoutElementField(node))
            {
                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                if (node.preferredWidth  >= 0) le.preferredWidth  = node.preferredWidth;
                if (node.preferredHeight >= 0) le.preferredHeight = node.preferredHeight;
                if (node.flexibleWidth   >= 0) le.flexibleWidth   = node.flexibleWidth;
                if (node.flexibleHeight  >= 0) le.flexibleHeight  = node.flexibleHeight;
            }

            // Scroll: 고유 구조라 별도 처리 (Viewport+Content+ScrollRect 내부 Content 의 VLayout 을 사용)
            if (node.scroll)
            {
                BuildScroll(go, node);
                return;
            }

            // 자식·텍스트를 담을 부모 결정: layout 이 지정되면 **별도 Layout 래퍼 자식** 을 만들어 거기 담는다
            var kind = (node.layout ?? "").ToLowerInvariant();
            var hasLayout = kind == "vertical" || kind == "horizontal";
            var hasChildren = node.children != null && node.children.Length > 0;
            var hasText = !string.IsNullOrEmpty(node.text);

            GameObject contentParent = go;
            if (hasLayout && (hasChildren || hasText))
            {
                var wrapperName = kind == "vertical" ? "VLayout" : "HLayout";
                contentParent = CreateChild(go, wrapperName);
                var rt = (RectTransform)contentParent.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                AttachLayoutGroup(contentParent, kind, node);
            }

            // Text: Txt 자식 생성 (layout 래퍼 있으면 그 안, 없으면 go 직속)
            if (hasText)
            {
                var txtGo = CreateChild(contentParent, "Txt");
                var rt = (RectTransform)txtGo.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                var tmp = txtGo.AddComponent<TextMeshProUGUI>();
                tmp.text = node.text;
                if (node.fontSize > 0) tmp.fontSize = node.fontSize;
                tmp.color = ParseColor(node.textColor, defaultValue: new Color(0.07f, 0.07f, 0.07f, 1));
                if (Enum.TryParse<TextAlignmentOptions>(node.textAlign, ignoreCase: true, out var al))
                    tmp.alignment = al;
                else
                    tmp.alignment = TextAlignmentOptions.Center;

                // Font: 노드별 fontPath 지정 시 로드. Analyzer 가 manifest.referenceFont 를 각 노드에 전파
                if (!string.IsNullOrEmpty(node.fontPath))
                {
                    var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(node.fontPath);
                    if (font != null) tmp.font = font;
                }
            }

            // 자식 재귀
            if (hasChildren)
            {
                foreach (var child in node.children)
                {
                    if (child == null) continue;
                    var childGo = CreateChild(contentParent, string.IsNullOrEmpty(child.name) ? "Child" : child.name);
                    ApplyNode(childGo, child, isRoot: false);
                }
            }
        }

        /// <summary>
        /// 지정된 GameObject 에 Vertical/Horizontal LayoutGroup 컴포넌트 부착 및 설정.
        /// 이 GameObject 는 ""레이아웃 전담 래퍼"" 로 써야 하며 box(Image/Outline) 는 부착하지 않는다
        /// </summary>
        private static void AttachLayoutGroup(GameObject go, string kind, Node node)
        {
            var padL = 0; var padR = 0; var padT = 0; var padB = 0;
            if (node.padding != null && node.padding.Length >= 4)
            {
                padL = (int)node.padding[0];
                padR = (int)node.padding[1];
                padT = (int)node.padding[2];
                padB = (int)node.padding[3];
            }

            HorizontalOrVerticalLayoutGroup g;
            if (kind == "vertical")
            {
                var v = go.AddComponent<VerticalLayoutGroup>();
                v.childControlWidth = true;
                v.childControlHeight = true;
                v.childForceExpandWidth = true;
                v.childForceExpandHeight = false;
                g = v;
            }
            else
            {
                var h = go.AddComponent<HorizontalLayoutGroup>();
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandWidth = false;
                h.childForceExpandHeight = true;
                g = h;
            }

            g.spacing = node.spacing;
            g.padding = new RectOffset(padL, padR, padT, padB);
        }

        /// <summary>
        /// 배경 박스(어두운) + Fill 이미지(filled type, amount) 로 세로/가로 게이지 생성.
        /// sprite 없으면 단색 Image 로. 레퍼런스 매칭 시 sprite 필드 우선
        /// </summary>
        private static void BuildProgressBar(GameObject go, Node node)
        {
            // 배경 (go 자체)
            var bgImg = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            bgImg.color = ParseColor(node.color, defaultValue: new Color(0.12f, 0.12f, 0.14f, 1f));

            // Fill 자식
            var fillGo = CreateChild(go, "Fill");
            var rt = (RectTransform)fillGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(4, 4);
            rt.offsetMax = new Vector2(-4, -4);

            var fillImg = fillGo.AddComponent<Image>();
            fillImg.color = ParseColor(node.fillColor, defaultValue: new Color(1f, 0.45f, 0.15f, 1f));
            if (!string.IsNullOrEmpty(node.sprite))
            {
                var sp = AssetDatabase.LoadAssetAtPath<Sprite>(node.sprite);
                if (sp != null) fillImg.sprite = sp;
            }
            fillImg.type = Image.Type.Filled;
            var dir = (node.fillDirection ?? "vertical").ToLowerInvariant();
            fillImg.fillMethod = dir == "horizontal"
                ? Image.FillMethod.Horizontal
                : Image.FillMethod.Vertical;
            fillImg.fillOrigin = dir == "horizontal" ? 0 : 0; // 0 = Left / Bottom
            fillImg.fillAmount = Mathf.Clamp01(node.fillAmount);
        }

        private static void BuildScroll(GameObject go, Node node)
        {
            // Viewport (Image 투명 + RectMask2D)
            var viewport = CreateChild(go, "Viewport");
            var viewportRt = (RectTransform)viewport.transform;
            viewportRt.anchorMin = Vector2.zero;
            viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;
            var viewportImg = viewport.AddComponent<Image>();
            viewportImg.color = new Color(1, 1, 1, 0);
            viewport.AddComponent<RectMask2D>();

            // Content — scrollLayout 에 따라 Vertical / Horizontal / Grid 적용
            var content = CreateChild(viewport, "Content");
            var contentRt = (RectTransform)content.transform;

            var layoutKind = (node.scrollLayout ?? "vertical").ToLowerInvariant();
            var fitter = content.AddComponent<ContentSizeFitter>();

            if (layoutKind == "horizontal")
            {
                contentRt.anchorMin = new Vector2(0, 0);
                contentRt.anchorMax = new Vector2(0, 1);
                contentRt.pivot = new Vector2(0, 0.5f);
                contentRt.offsetMin = Vector2.zero;
                contentRt.offsetMax = Vector2.zero;
                var hlg = content.AddComponent<HorizontalLayoutGroup>();
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = true;
                hlg.spacing = 8;
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            else if (layoutKind == "grid")
            {
                contentRt.anchorMin = new Vector2(0, 1);
                contentRt.anchorMax = new Vector2(1, 1);
                contentRt.pivot = new Vector2(0.5f, 1);
                contentRt.offsetMin = Vector2.zero;
                contentRt.offsetMax = Vector2.zero;
                var grid = content.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(
                    node.scrollCellWidth  > 0 ? node.scrollCellWidth  : 200,
                    node.scrollCellHeight > 0 ? node.scrollCellHeight : 200);
                grid.spacing = new Vector2(8, 8);
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            else
            {
                contentRt.anchorMin = new Vector2(0, 1);
                contentRt.anchorMax = new Vector2(1, 1);
                contentRt.pivot = new Vector2(0.5f, 1);
                contentRt.offsetMin = Vector2.zero;
                contentRt.offsetMax = Vector2.zero;
                var vlg = content.AddComponent<VerticalLayoutGroup>();
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.spacing = 8;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            // ScrollRect
            var sr = go.AddComponent<ScrollRect>();
            sr.viewport = viewportRt;
            sr.content = contentRt;
            var dir = (node.scrollDirection ?? "vertical").ToLowerInvariant();
            sr.horizontal = dir == "horizontal";
            sr.vertical = dir != "horizontal";
            sr.movementType = ScrollRect.MovementType.Elastic;

            // scroll 노드의 children 은 Content 하위로 배치 (추후 확장)
            if (node.children != null)
            {
                foreach (var child in node.children)
                {
                    if (child == null) continue;
                    var childGo = CreateChild(content, string.IsNullOrEmpty(child.name) ? "Item" : child.name);
                    ApplyNode(childGo, child, isRoot: false);
                }
            }
        }

        private static GameObject CreateChild(GameObject parent, string name)
        {
            return CreateChild(parent.transform, name);
        }

        private static GameObject CreateChild(Component parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(100, 100);
            return go;
        }

        private static bool HasAnyLayoutElementField(Node n) =>
            n.preferredWidth >= 0 || n.preferredHeight >= 0 ||
            n.flexibleWidth >= 0 || n.flexibleHeight >= 0;

        /// <summary>
        /// Overlay 노드의 RectTransform 에 anchor/pivot 세팅 + offset·size 적용.
        /// stretch 면 parent 를 가득 채우고 offset 을 margin 으로 해석
        /// </summary>
        private static void ApplyOverlayRect(RectTransform rt, Node n)
        {
            var kind = (n.anchor ?? "center").ToLowerInvariant();
            Vector2 aMin, aMax, pivot;
            switch (kind)
            {
                case "topleft":     aMin = new(0, 1); aMax = new(0, 1); pivot = new(0, 1); break;
                case "top":         aMin = new(0.5f, 1); aMax = new(0.5f, 1); pivot = new(0.5f, 1); break;
                case "topright":    aMin = new(1, 1); aMax = new(1, 1); pivot = new(1, 1); break;
                case "left":        aMin = new(0, 0.5f); aMax = new(0, 0.5f); pivot = new(0, 0.5f); break;
                case "right":       aMin = new(1, 0.5f); aMax = new(1, 0.5f); pivot = new(1, 0.5f); break;
                case "bottomleft":  aMin = new(0, 0); aMax = new(0, 0); pivot = new(0, 0); break;
                case "bottom":      aMin = new(0.5f, 0); aMax = new(0.5f, 0); pivot = new(0.5f, 0); break;
                case "bottomright": aMin = new(1, 0); aMax = new(1, 0); pivot = new(1, 0); break;
                case "stretch":     aMin = new(0, 0); aMax = new(1, 1); pivot = new(0.5f, 0.5f); break;
                case "center":
                default:            aMin = new(0.5f, 0.5f); aMax = new(0.5f, 0.5f); pivot = new(0.5f, 0.5f); break;
            }
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.pivot = pivot;

            if (kind == "stretch")
            {
                // offset 을 4변 margin 으로 사용. preferredWidth/Height 무시 (parent fill)
                rt.offsetMin = new Vector2(n.offsetX, n.offsetY);
                rt.offsetMax = new Vector2(-n.offsetX, -n.offsetY);
            }
            else
            {
                var w = n.preferredWidth  > 0 ? n.preferredWidth  : rt.sizeDelta.x;
                var h = n.preferredHeight > 0 ? n.preferredHeight : rt.sizeDelta.y;
                rt.sizeDelta = new Vector2(w, h);
                rt.anchoredPosition = new Vector2(n.offsetX, n.offsetY);
            }
        }

        private static Color ParseColor(string hex, Color defaultValue)
        {
            if (string.IsNullOrEmpty(hex)) return defaultValue;
            if (ColorUtility.TryParseHtmlString(hex, out var c)) return c;
            return defaultValue;
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

        private static Result Fail(string msg) => new() { success = false, error = msg };

        /// <summary>
        /// Claude 응답에서 fenced JSON 블록 추출. "...```json\n{...}\n```..." 형태 지원.
        /// 블록 없으면 원본을 그대로 JSON 으로 가정
        /// </summary>
        public static string ExtractJsonBlock(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var startIdx = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            int bodyStart;
            if (startIdx >= 0)
            {
                bodyStart = raw.IndexOf('\n', startIdx) + 1;
            }
            else
            {
                // "```" (no lang) 도 지원
                startIdx = raw.IndexOf("```", StringComparison.Ordinal);
                if (startIdx < 0) return raw.Trim();
                bodyStart = raw.IndexOf('\n', startIdx) + 1;
            }
            var endIdx = raw.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (endIdx < 0) return raw.Substring(bodyStart).Trim();
            return raw.Substring(bodyStart, endIdx - bodyStart).Trim();
        }
    }
}
