using System.IO;
using UniMCP.Editor.PrefabHook;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// TreeSpec JSON 을 받아 가상 구조도(박스+레이어 시뮬레이션) 를 IMGUI 로 렌더.
    /// 실제 프리팹 생성 전에 구조·비율·계층을 한눈에 확인하기 위한 프리뷰
    /// </summary>
    public class PrefabTreePreviewWindow : EditorWindow
    {
        private string _treeSpecJson;
        private TreeGenerator.TreeSpec _spec;
        private string _parseError;
        private Vector2 _scroll;
        private System.Action<string> _onNodeClicked;
        private readonly System.Collections.Generic.List<(Rect rect, string name)> _hitRects
            = new System.Collections.Generic.List<(Rect, string)>();

        [MenuItem("UniMCP/Prefab Tree Preview")]
        private static void OpenMenu() => GetWindow<PrefabTreePreviewWindow>("Prefab Preview");

        public static void ShowFor(string treeSpecJson, System.Action<string> onNodeClicked = null)
        {
            var window = GetWindow<PrefabTreePreviewWindow>("Prefab Preview");
            window.minSize = new Vector2(600, 800);
            window.SetSpec(treeSpecJson);
            window._onNodeClicked = onNodeClicked;
            window.Show();
            window.Focus();
        }

        public void SetSpec(string json)
        {
            _treeSpecJson = json;
            _parseError = null;
            _spec = null;
            if (string.IsNullOrEmpty(json)) return;
            try { _spec = JsonUtility.FromJson<TreeGenerator.TreeSpec>(json); }
            catch (System.Exception e) { _parseError = e.Message; }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
                EditorGUILayout.LabelField("Prefab Layout Preview", titleStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", GUILayout.Width(80)))
                    SetSpec(_treeSpecJson);
            }

            EditorGUILayout.LabelField(
                "TreeSpec 기반 가상 렌더링입니다. 실제 Unity 결과와 픽셀 단위로 일치하지는 않지만 구조·비율 확인에 충분합니다.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(6);

            if (_spec == null || _spec.root == null)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(_parseError)
                        ? "TreeSpec 이 없습니다."
                        : "TreeSpec 파싱 실패: " + _parseError,
                    MessageType.Warning);
                return;
            }

            var fullRect = GUILayoutUtility.GetRect(
                0, 0, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));

            // 배경 (다크 그리드 느낌)
            EditorGUI.DrawRect(fullRect, EditorGUIUtility.isProSkin
                ? new Color(0.11f, 0.13f, 0.16f, 1f)
                : new Color(0.85f, 0.85f, 0.88f, 1f));

            var rootSize = GetRootSize();
            float aspect = rootSize.x / rootSize.y;
            var canvasRect = FitAspect(fullRect, aspect);
            float scale = canvasRect.width / rootSize.x;

            // 캔버스 영역 (프리팹 루트 sizeDelta)
            EditorGUI.DrawRect(canvasRect, new Color(0.20f, 0.22f, 0.26f, 1f));

            _hitRects.Clear();
            RenderNode(_spec.root, canvasRect, scale, depth: 0);

            // 모든 렌더 이후 클릭 처리: mouse 위치를 포함하는 rect 중 **가장 작은 면적** (=가장 깊은 노드) 선택
            var evt = Event.current;
            if (_onNodeClicked != null && evt.type == EventType.MouseDown && evt.button == 0
                && fullRect.Contains(evt.mousePosition))
            {
                string bestName = null;
                float bestArea = float.MaxValue;
                foreach (var (r, n) in _hitRects)
                {
                    if (!r.Contains(evt.mousePosition)) continue;
                    var area = r.width * r.height;
                    if (area < bestArea) { bestArea = area; bestName = n; }
                }
                if (!string.IsNullOrEmpty(bestName))
                {
                    _onNodeClicked.Invoke(bestName);
                    evt.Use();
                }
            }
        }

        private Vector2 GetRootSize()
        {
            if (_spec.size != null && _spec.size.Length >= 2 && _spec.size[0] > 0 && _spec.size[1] > 0)
                return new Vector2(_spec.size[0], _spec.size[1]);
            return new Vector2(1080, 1920);
        }

        private void RenderNode(TreeGenerator.Node node, Rect rect, float scale, int depth)
        {
            if (node == null) return;

            // 클릭 대상 등록 — 실제 클릭 판정은 OnGUI 끝에서 ""가장 깊은(작은) rect"" 로 판별
            if (!string.IsNullOrEmpty(node.name))
                _hitRects.Add((rect, node.name));

            if (_onNodeClicked != null)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            // 노드 박스 그리기
            if (!string.IsNullOrEmpty(node.nestedPrefab))
            {
                // Nested prefab: 대시 테두리 느낌 + 이름 라벨
                var fill = new Color(0.72f, 0.78f, 0.85f, 0.6f);
                EditorGUI.DrawRect(rect, fill);
                DrawBorder(rect, new Color(0.30f, 0.35f, 0.45f), 1.5f);

                var name = Path.GetFileNameWithoutExtension(node.nestedPrefab);
                DrawLabelMultiline(rect,
                    $"⟦ {name} ⟧\n{node.name ?? ""}",
                    alignment: TextAnchor.MiddleCenter,
                    italic: true,
                    fontSize: Mathf.Clamp((int)(16 * scale * 1.2f), 9, 14),
                    color: new Color(0.15f, 0.20f, 0.30f));
            }
            else if (node.box || node.button)
            {
                var fillCol   = ParseColor(node.color, new Color(1, 1, 1, 1));
                var strokeCol = ParseColor(node.outlineColor, new Color(0.07f, 0.07f, 0.07f, 1));
                EditorGUI.DrawRect(rect, fillCol);
                DrawBorder(rect, strokeCol, node.button ? 2f : 1f);
            }
            else
            {
                // 레이아웃 전용 컨테이너: 테두리 없이 (가이드용 점선만 depth=1 일 때)
                if (depth == 0)
                {
                    EditorGUI.DrawRect(rect, new Color(0.25f, 0.28f, 0.32f, 0.3f));
                    DrawBorder(rect, new Color(0.4f, 0.4f, 0.45f, 0.6f), 1f);
                }
            }

            // 노드 텍스트
            if (!string.IsNullOrEmpty(node.text))
            {
                var col = ParseColor(node.textColor, Color.black);
                int fs = Mathf.Clamp((int)((node.fontSize > 0 ? node.fontSize : 40) * scale * 0.55f), 8, 24);
                DrawLabel(rect, node.text, ParseAlign(node.textAlign), color: col, fontSize: fs, bold: false);
            }
            else if (!string.IsNullOrEmpty(node.name) && depth > 0 && node.children == null)
            {
                // 텍스트 없는 리프 노드의 이름만 회색으로 희미하게
                DrawLabel(rect, node.name, TextAnchor.MiddleCenter,
                    color: new Color(0.4f, 0.4f, 0.4f), fontSize: 9, italic: true);
            }

            // 자식 재귀는 nestedPrefab / scroll leaf 아닐 때만
            if (!string.IsNullOrEmpty(node.nestedPrefab)) return;

            if (node.scroll)
            {
                DrawLabel(rect, "↕ SCROLL", TextAnchor.MiddleCenter,
                    color: new Color(0.35f, 0.35f, 0.35f), fontSize: 11, italic: true);
                return;
            }

            if (node.children == null || node.children.Length == 0) return;

            var inner = ApplyPadding(rect, node.padding, scale);
            var kind = (node.layout ?? "").ToLowerInvariant();
            if (kind == "vertical")        LayoutVertical(node, inner, scale, depth);
            else if (kind == "horizontal") LayoutHorizontal(node, inner, scale, depth);
            else
            {
                foreach (var c in node.children) RenderNode(c, inner, scale, depth + 1);
            }
        }

        private void LayoutVertical(TreeGenerator.Node parent, Rect inner, float scale, int depth)
        {
            float spacingPx = (parent.spacing > 0 ? parent.spacing : 0) * scale;
            int n = parent.children.Length;
            float totalSpacing = spacingPx * Mathf.Max(0, n - 1);
            float avail = inner.height - totalSpacing;

            float totalPref = 0, totalFlex = 0;
            foreach (var c in parent.children)
            {
                if (c.preferredHeight > 0) totalPref += c.preferredHeight * scale;
                if (c.flexibleHeight > 0) totalFlex += c.flexibleHeight;
            }
            float flexSpace = Mathf.Max(0, avail - totalPref);

            float y = inner.y;
            foreach (var c in parent.children)
            {
                float h;
                if (c.preferredHeight > 0)            h = c.preferredHeight * scale;
                else if (c.flexibleHeight > 0 && totalFlex > 0) h = (c.flexibleHeight / totalFlex) * flexSpace;
                else                                  h = 40f * scale;

                RenderNode(c, new Rect(inner.x, y, inner.width, h), scale, depth + 1);
                y += h + spacingPx;
            }
        }

        private void LayoutHorizontal(TreeGenerator.Node parent, Rect inner, float scale, int depth)
        {
            float spacingPx = (parent.spacing > 0 ? parent.spacing : 0) * scale;
            int n = parent.children.Length;
            float totalSpacing = spacingPx * Mathf.Max(0, n - 1);
            float avail = inner.width - totalSpacing;

            float totalPref = 0, totalFlex = 0;
            foreach (var c in parent.children)
            {
                if (c.preferredWidth > 0) totalPref += c.preferredWidth * scale;
                if (c.flexibleWidth > 0) totalFlex += c.flexibleWidth;
            }
            float flexSpace = Mathf.Max(0, avail - totalPref);

            float x = inner.x;
            foreach (var c in parent.children)
            {
                float w;
                if (c.preferredWidth > 0)             w = c.preferredWidth * scale;
                else if (c.flexibleWidth > 0 && totalFlex > 0) w = (c.flexibleWidth / totalFlex) * flexSpace;
                else                                  w = 40f * scale;

                RenderNode(c, new Rect(x, inner.y, w, inner.height), scale, depth + 1);
                x += w + spacingPx;
            }
        }

        // ---- helpers ----

        private static Rect ApplyPadding(Rect r, float[] pad, float scale)
        {
            if (pad == null || pad.Length < 4) return r;
            return new Rect(
                r.x + pad[0] * scale,
                r.y + pad[2] * scale,
                r.width  - (pad[0] + pad[1]) * scale,
                r.height - (pad[2] + pad[3]) * scale);
        }

        private static Rect FitAspect(Rect container, float aspect)
        {
            var cw = container.width;
            var ch = container.height;
            float rw = cw, rh = cw / Mathf.Max(0.0001f, aspect);
            if (rh > ch) { rh = ch; rw = ch * aspect; }
            return new Rect(
                container.x + (cw - rw) * 0.5f,
                container.y + (ch - rh) * 0.5f,
                rw, rh);
        }

        private static void DrawBorder(Rect r, Color col, float th)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, th), col);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - th, r.width, th), col);
            EditorGUI.DrawRect(new Rect(r.x, r.y, th, r.height), col);
            EditorGUI.DrawRect(new Rect(r.xMax - th, r.y, th, r.height), col);
        }

        private static void DrawLabel(Rect r, string text, TextAnchor align,
            Color color, int fontSize, bool italic = false, bool bold = false)
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                alignment = align,
                fontSize = fontSize,
                wordWrap = true,
                richText = false,
                fontStyle = italic ? (bold ? FontStyle.BoldAndItalic : FontStyle.Italic)
                                   : (bold ? FontStyle.Bold : FontStyle.Normal),
                normal = { textColor = color },
            };
            GUI.Label(r, text, style);
        }

        private static void DrawLabelMultiline(Rect r, string text, TextAnchor alignment,
            bool italic, int fontSize, Color color)
        {
            DrawLabel(r, text, alignment, color, fontSize, italic);
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
        }

        private static TextAnchor ParseAlign(string s)
        {
            if (string.IsNullOrEmpty(s)) return TextAnchor.MiddleCenter;
            var v = s.Replace(" ", "").Replace("-", "").ToLowerInvariant();
            return v switch
            {
                "topleft"    or "upperleft"  => TextAnchor.UpperLeft,
                "topcenter"  or "uppercenter" or "center top" => TextAnchor.UpperCenter,
                "topright"   or "upperright" => TextAnchor.UpperRight,
                "midlineleft" or "middleleft" or "left" => TextAnchor.MiddleLeft,
                "midcenter"  or "middlecenter" or "center" => TextAnchor.MiddleCenter,
                "midright"   or "middleright" or "right"   => TextAnchor.MiddleRight,
                "bottomleft" or "lowerleft"  => TextAnchor.LowerLeft,
                "bottomcenter" or "lowercenter" => TextAnchor.LowerCenter,
                "bottomright" or "lowerright" => TextAnchor.LowerRight,
                _ => TextAnchor.MiddleCenter,
            };
        }
    }
}
