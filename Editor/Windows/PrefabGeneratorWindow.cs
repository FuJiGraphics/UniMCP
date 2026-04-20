using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Logging;
using UniMCP.Editor.PrefabHook;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// 레이아웃 이미지 한 장과 스킬을 받아 프리팹을 생성하는 전용 윈도우.
    /// Run 시 UniMcpRunQueue 로 작업을 예약하고, Stop 시 작업 취소 + 해당 작업이 만든 모든 에셋을 삭제한다
    /// </summary>
    public class PrefabGeneratorWindow : EditorWindow
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".tif", ".tiff", ".exr" };

        [SerializeField] private string _inputImagePath;
        [SerializeField] private string _outputFolder;
        [SerializeField] private string _userHint;
        [SerializeField] private List<string> _referencePrefabs = new List<string>();
        [SerializeField] private string _referenceFontPath;
        [SerializeField] private string _lastRunAt;
        private Texture2D _previewTex;
        private string _previewTexPath;

        private bool _isRunning;
        private Guid _currentJobId;
        private string _currentManifestPath;
        private UniMcpSkill _myQueuedSkill;
        private bool _isMyJobRunning;
        private double _runStartedAt;
        private int _thinkingDots;
        private string _status;
        private Vector2 _statusScroll;

        private static string ManifestRoot
        {
            get
            {
                var project = Path.GetDirectoryName(Application.dataPath);
                return Path.Combine(project, "Library", "UniMCP", "PrefabGen");
            }
        }

        private static string LogRoot
        {
            get
            {
                var project = Path.GetDirectoryName(Application.dataPath);
                return Path.Combine(project, "Library", "UniMCP", "Logs", "PrefabGen");
            }
        }

        [SerializeField] private string _currentLogPath;

        [MenuItem("UniMCP/Prefab Generator")]
        private static void Open()
        {
            try
            {
                // 이전에 생성됐다 hidden 상태로 남은 인스턴스 제거 — DockUtil.TryDock 실패 후 떠도는 case 대응
                foreach (var w in Resources.FindObjectsOfTypeAll<PrefabGeneratorWindow>())
                {
                    if (w != null && !w.hasFocus && (w.position.width < 1f || w.position.height < 1f))
                        w.Close();
                }

                var window = GetWindow<PrefabGeneratorWindow>("Prefab Generator");
                window.minSize = new Vector2(540, 600);
                window.Show();
                window.Focus();
            }
            catch (Exception e)
            {
                UniMcpLogger.Exception(e);
                EditorUtility.DisplayDialog(
                    "Prefab Generator",
                    "윈도우 열기 실패:\n" + e.Message,
                    "OK");
            }
        }

        internal static PrefabGeneratorWindow GetOrCreateWindow() => GetWindow<PrefabGeneratorWindow>("Prefab Generator");

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            UniMcpRunQueue.QueueChanged += OnQueueChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            UniMcpRunQueue.QueueChanged -= OnQueueChanged;
        }

        private void OnQueueChanged()
        {
            var isMine = _myQueuedSkill != null
                         && UniMcpRunQueue.RunningSkill == _myQueuedSkill;
            if (isMine && !_isMyJobRunning)
            {
                _runStartedAt = EditorApplication.timeSinceStartup;
                _thinkingDots = 0;
            }
            _isMyJobRunning = isMine;
            Repaint();
        }

        private void OnEditorUpdate()
        {
            if (!_isRunning)
                return;

            var elapsed = EditorApplication.timeSinceStartup - _runStartedAt;
            var newDots = ((int)(elapsed * 2)) % 4;
            if (newDots != _thinkingDots)
            {
                _thinkingDots = newDots;
                Repaint();
            }
        }

        private static Color AccentBlue  => new(0.40f, 0.70f, 1.00f);
        private static Color AccentGreen => new(0.35f, 0.78f, 0.42f);
        private static Color AccentRed   => new(0.88f, 0.38f, 0.38f);
        private static Color PanelTint   => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.03f)
            : new Color(0f, 0f, 0f, 0.03f);
        private static Color Separator   => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.08f)
            : new Color(0f, 0f, 0f, 0.12f);

        [SerializeField] private bool _advancedExpanded;
        [SerializeField] private Vector2 _rootScroll;

        private static GUIStyle CardBoxStyle => new GUIStyle(EditorStyles.helpBox)
        {
            padding = new RectOffset(12, 12, 12, 12),
            margin = new RectOffset(0, 0, 0, 0),
        };

        private static GUIStyle CardHeaderStyle => new GUIStyle(EditorStyles.miniBoldLabel)
        {
            fontSize = 9,
            fontStyle = FontStyle.Bold,
            normal = { textColor = EditorGUIUtility.isProSkin
                ? new Color(0.60f, 0.60f, 0.60f)
                : new Color(0.35f, 0.35f, 0.35f) },
        };

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            DrawHeader();
            EditorGUILayout.Space(10);

            _rootScroll = EditorGUILayout.BeginScrollView(_rootScroll);

            // 1단: SOURCE + TARGET 나란히
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.48f)))
                    DrawCard_Source();

                GUILayout.Space(8);

                using (new EditorGUILayout.VerticalScope())
                    DrawCard_Target();
            }

            EditorGUILayout.Space(10);

            // 2단: REFERENCE ASSETS foldout (optional 자산 먼저 보여줌)
            DrawCard_Advanced();

            EditorGUILayout.Space(10);

            // 3단: INTENT 카드 (HINT + Generate 버튼 통합, 실행 트리거)
            DrawCard_Intent();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8);

            // 4단: STATUS / APPROVAL — 남은 공간 전부
            DrawCard_Status();
            EditorGUILayout.Space(4);
        }

        private void DrawCard_Source()
        {
            using (new EditorGUILayout.VerticalScope(CardBoxStyle))
            {
                EditorGUILayout.LabelField("SOURCE  ·  LAYOUT IMAGE", CardHeaderStyle);
                EditorGUILayout.Space(4);
                DrawImageSlot();
            }
        }

        private void DrawCard_Target()
        {
            using (new EditorGUILayout.VerticalScope(CardBoxStyle))
            {
                EditorGUILayout.LabelField("TARGET  ·  OUTPUT FOLDER", CardHeaderStyle);
                EditorGUILayout.Space(4);
                DrawOutputFolder();
            }
        }

        private void DrawCard_Intent()
        {
            var intentBox = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(14, 14, 14, 14),
            };

            using (new EditorGUILayout.VerticalScope(intentBox))
            {
                var titleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13,
                    normal = { textColor = AccentBlue },
                };
                EditorGUILayout.LabelField("▷  DESCRIBE INTENT", titleStyle);

                var descStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    wordWrap = true,
                    normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
                };
                EditorGUILayout.LabelField(
                    "이미지만으로 애매한 부분을 자연어로 보강하세요. 예: \"Header 는 Scroll 안에 포함\", \"하단 리스트는 Grid 로\", \"타이틀 폰트 크게\"",
                    descStyle);

                EditorGUILayout.Space(6);

                var taStyle = new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true,
                    fontSize = 12,
                    padding = new RectOffset(10, 10, 8, 8),
                };

                bool hintEmpty = string.IsNullOrEmpty(_userHint);
                var placeholderStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.50f, 0.50f, 0.50f) },
                    padding = new RectOffset(14, 14, 12, 12),
                    wordWrap = true,
                };

                var taRect = GUILayoutUtility.GetRect(
                    GUIContent.none, taStyle,
                    GUILayout.MinHeight(110), GUILayout.ExpandWidth(true));

                _userHint = EditorGUI.TextArea(taRect, _userHint ?? "", taStyle);

                if (hintEmpty && GUI.GetNameOfFocusedControl() != "HINT_AREA")
                {
                    GUI.Label(taRect,
                        "이미지 해석을 이렇게 해달라... 라고 자유롭게 입력 (비워둬도 OK)",
                        placeholderStyle);
                }

                EditorGUILayout.Space(10);

                // 상태 기반 통합 액션 버튼 — 모든 실행/수락/재시도는 여기 한 곳에서
                DrawStateActionButtons();

                if (!string.IsNullOrEmpty(_lastRunAt))
                {
                    EditorGUILayout.Space(2);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Last run: {_lastRunAt}", EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        GUI.enabled = !string.IsNullOrEmpty(_currentLogPath) && File.Exists(_currentLogPath);
                        if (GUILayout.Button("Open Log", EditorStyles.miniButton, GUILayout.Width(80)))
                            EditorUtility.RevealInFinder(_currentLogPath);
                        GUI.enabled = true;
                        if (GUILayout.Button("Logs…", EditorStyles.miniButton, GUILayout.Width(60)))
                            OpenLogsFolder();
                    }
                }
            }
        }

        /// <summary>
        /// 파이프라인 상태에 따라 한 자리에서 모든 주요 액션 버튼 렌더.
        /// Idle → Generate / Running → Stop / WaitingApproval → Accept Structure + Refine / WaitingFinalAccept → Accept Prefab + Refine
        /// </summary>
        private void DrawStateActionButtons()
        {
            var btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                fixedHeight = 40,
            };

            var prev = GUI.backgroundColor;

            // ─ 승인 대기 (Analyzer 결과 검토)
            if (!string.IsNullOrEmpty(_pendingApprovalTreeSpec))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.backgroundColor = AccentGreen;
                    if (GUILayout.Button("✓  Accept Structure  —  Generate Prefab", btnStyle))
                    {
                        var spec = _pendingApprovalTreeSpec;
                        _pendingApprovalTreeSpec = null;
                        _pipeline?.ApproveTreeSpec(spec);
                    }
                    GUI.backgroundColor = prev;

                    GUI.backgroundColor = AccentRed;
                    if (GUILayout.Button("Cancel", btnStyle, GUILayout.Width(110)))
                    {
                        _pendingApprovalTreeSpec = null;
                        TriggerStop();
                    }
                    GUI.backgroundColor = prev;
                }
                return;
            }

            // ─ 최종 수락 대기 (Generate + Reviewer 끝난 뒤 유저 결정)
            // 유저는 [Refine] 로 힌트 추가 수정 반복 가능, 만족하면 [Done] 으로 세션 종료
            if (_pendingFinalState != null)
            {
                var hasHint = !string.IsNullOrWhiteSpace(_userHint);
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Refine 이 주된 액션 (녹색 · 가장 왼쪽 · 넓게)
                    GUI.enabled = hasHint;
                    GUI.backgroundColor = hasHint ? AccentGreen : prev;
                    if (GUILayout.Button(hasHint ? "↻  Refine with hint" : "↻  Type hint above to refine",
                                         btnStyle))
                    {
                        var hint = _userHint;
                        _pendingFinalState = null;
                        _pipeline?.RefineWithHint(hint);
                    }
                    GUI.backgroundColor = prev;
                    GUI.enabled = true;

                    // Review Sizes — 레퍼런스 이미지 비교로 크기 자동 보정. 힌트가 있으면 함께 반영
                    GUI.backgroundColor = AccentBlue;
                    var reviewLabel = hasHint ? "🔎  Review + Apply Hint" : "🔎  Review Sizes";
                    if (GUILayout.Button(reviewLabel, btnStyle, GUILayout.Width(200)))
                    {
                        var hint = _userHint;
                        _pendingFinalState = null;
                        _pipeline?.RunSizeReview(hint);
                    }
                    GUI.backgroundColor = prev;

                    // Done — 만족하고 세션 종료 (prefab 유지)
                    if (GUILayout.Button("✓  Done", btnStyle, GUILayout.Width(110)))
                    {
                        _pendingFinalState = null;
                        _pipeline?.AcceptFinal();
                    }

                    GUI.backgroundColor = AccentRed;
                    if (GUILayout.Button("Cancel", btnStyle, GUILayout.Width(90)))
                    {
                        _pendingFinalState = null;
                        TriggerStop();
                    }
                    GUI.backgroundColor = prev;
                }

                EditorGUILayout.Space(2);
                var hintLine = new GUIStyle(EditorStyles.miniLabel)
                {
                    wordWrap = true,
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
                };
                EditorGUILayout.LabelField(
                    "[Review Sizes] = 레퍼런스 비교로 크기 자동 보정. [Refine] = 힌트 기반 구조/내용 재생성. 프리뷰에서 노드 클릭하면 @NodeName 삽입.",
                    hintLine);
                return;
            }

            // ─ 실행 중 (Stop 만)
            if (_isRunning)
            {
                GUI.backgroundColor = AccentRed;
                if (GUILayout.Button("■  Stop", btnStyle))
                    TriggerStop();
                GUI.backgroundColor = prev;
                return;
            }

            // ─ Idle (Generate)
            var canRun = !string.IsNullOrEmpty(_inputImagePath);
            GUI.enabled = canRun;
            GUI.backgroundColor = canRun ? AccentGreen : prev;
            if (GUILayout.Button(canRun ? "▶  Generate Prefab" : "Drop layout image first", btnStyle))
                TriggerRun();
            GUI.backgroundColor = prev;
            GUI.enabled = true;
        }

        private void DrawCard_Advanced()
        {
            var foldoutStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontStyle = FontStyle.Bold,
            };
            _advancedExpanded = EditorGUILayout.Foldout(_advancedExpanded,
                "▷  Reference Assets  (prefabs · font)", true, foldoutStyle);

            if (!_advancedExpanded) return;

            using (new EditorGUILayout.VerticalScope(CardBoxStyle))
            {
                DrawReferencePrefabs();
                EditorGUILayout.Space(8);
                DrawReferenceFont();
            }
        }

        private void DrawCard_Status()
        {
            DrawStatus();
        }

        private void DrawHeader()
        {
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
            };
            var subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.72f, 0.72f, 0.72f)
                    : new Color(0.35f, 0.35f, 0.35f) },
            };

            EditorGUILayout.LabelField("Prefab Generator", titleStyle);
            EditorGUILayout.LabelField(
                "레이아웃 이미지를 넣고 스킬을 선택해 프리팹을 생성합니다.",
                subStyle);
        }

        private void DrawImageSlot()
        {
            var dropRect = GUILayoutUtility.GetRect(
                0, 140,
                GUILayout.ExpandWidth(true));

            var evt = Event.current;
            var hasImage = !string.IsNullOrEmpty(_inputImagePath);
            var hovering = dropRect.Contains(evt.mousePosition)
                && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform);

            DrawDropZoneFrame(dropRect, hovering, hasContent: hasImage);

            if (hasImage)
            {
                EnsurePreview();
                if (_previewTex != null)
                {
                    var pad = 8f;
                    var inner = new Rect(
                        dropRect.x + pad,
                        dropRect.y + pad,
                        dropRect.width - pad * 2,
                        dropRect.height - pad * 2);
                    var aspect = (float)_previewTex.width / Mathf.Max(1, _previewTex.height);
                    var drawRect = FitAspect(inner, aspect);
                    GUI.DrawTexture(drawRect, _previewTex, ScaleMode.ScaleToFit);
                }
                else
                {
                    var labelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                    };
                    GUI.Label(dropRect, _inputImagePath, labelStyle);
                }
            }
            else
            {
                DrawDropZoneHint(dropRect, hovering);
            }

            if (dropRect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
                else if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    var resolved = ExtractImagePath(DragAndDrop.objectReferences, DragAndDrop.paths);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        _inputImagePath = resolved;
                        InvalidatePreview();
                    }
                    evt.Use();
                    Repaint();
                }
            }

            if (hasImage)
            {
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var pathStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                    };
                    EditorGUILayout.LabelField(_inputImagePath, pathStyle);
                    if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(16)))
                    {
                        _inputImagePath = null;
                        InvalidatePreview();
                    }
                }
            }
        }

        private static string ExtractImagePath(UnityEngine.Object[] refs, string[] paths)
        {
            if (refs != null)
            {
                foreach (var obj in refs)
                {
                    if (obj == null) continue;
                    var p = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(p) && IsImageExtension(p)) return p;
                }
            }
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (IsImageExtension(p)) return p.Replace('\\', '/');
                }
            }
            return null;
        }

        private static bool IsImageExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ImageExtensions.Contains(ext);
        }

        private void EnsurePreview()
        {
            if (string.IsNullOrEmpty(_inputImagePath)) return;
            if (_previewTex != null && _previewTexPath == _inputImagePath) return;

            _previewTexPath = _inputImagePath;

            // Assets 하위면 AssetDatabase 로, 외부면 File.ReadAllBytes 로 로드
            if (_inputImagePath.StartsWith("Assets/"))
            {
                _previewTex = AssetDatabase.LoadAssetAtPath<Texture2D>(_inputImagePath);
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(_inputImagePath);
                var tex = new Texture2D(2, 2);
                tex.LoadImage(bytes);
                _previewTex = tex;
            }
            catch { _previewTex = null; }
        }

        private void InvalidatePreview()
        {
            _previewTex = null;
            _previewTexPath = null;
        }

        private void DrawOutputFolder()
        {
            var dropRect = GUILayoutUtility.GetRect(0, 80, GUILayout.ExpandWidth(true));

            var evt = Event.current;
            var hasFolder = !string.IsNullOrEmpty(_outputFolder);
            var hovering = dropRect.Contains(evt.mousePosition)
                && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform);

            DrawDropZoneFrame(dropRect, hovering, hasContent: hasFolder);

            var pad = 10f;
            var innerRect = new Rect(dropRect.x + pad, dropRect.y + pad,
                dropRect.width - pad * 2, dropRect.height - pad * 2);

            if (hasFolder)
            {
                var style = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.85f, 0.85f, 0.85f)
                        : new Color(0.2f, 0.2f, 0.2f) },
                };
                GUI.Label(innerRect, _outputFolder, style);
            }
            else
            {
                var titleStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = hovering
                        ? AccentBlue
                        : new Color(0.55f, 0.55f, 0.55f) },
                };
                GUI.Label(innerRect,
                    hovering ? "Release to set folder" : "Drop folder here\nor use Browse…",
                    titleStyle);
            }

            if (dropRect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
                else if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    var folder = ExtractFolderPath(DragAndDrop.objectReferences, DragAndDrop.paths);
                    if (!string.IsNullOrEmpty(folder))
                        _outputFolder = folder;
                    evt.Use();
                    Repaint();
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Browse…", GUILayout.Height(22)))
                    BrowseFolder();

                GUI.enabled = hasFolder;
                if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(22)))
                    _outputFolder = null;
                GUI.enabled = true;
            }
        }

        private void BrowseFolder()
        {
            var start = string.IsNullOrEmpty(_outputFolder)
                ? Path.Combine(Path.GetDirectoryName(Application.dataPath), "Assets")
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), _outputFolder);
            var picked = EditorUtility.OpenFolderPanel("Output Folder", start, "");
            if (string.IsNullOrEmpty(picked)) return;
            var rel = ToProjectRelative(picked);
            if (rel == null)
            {
                EditorUtility.DisplayDialog("Output Folder",
                    "프로젝트 내부 폴더만 선택 가능합니다.", "OK");
                return;
            }
            _outputFolder = rel;
        }

        private static string ExtractFolderPath(UnityEngine.Object[] refs, string[] paths)
        {
            if (refs != null)
            {
                foreach (var obj in refs)
                {
                    if (obj == null) continue;
                    var p = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p)) return p;
                }
            }
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    var norm = p.Replace('\\', '/');
                    if (Directory.Exists(norm))
                    {
                        var rel = ToProjectRelative(norm);
                        if (rel != null) return rel;
                    }
                }
            }
            return null;
        }

        private static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var norm = absolute.Replace('\\', '/').TrimEnd('/');
            var projRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            if (!norm.StartsWith(projRoot + "/") && norm != projRoot) return null;
            var rel = norm.Length == projRoot.Length ? "" : norm.Substring(projRoot.Length + 1);
            if (!rel.StartsWith("Assets") && !rel.StartsWith("Packages")) return null;
            return rel;
        }

        private void DrawReferencePrefabs()
        {
            EditorGUILayout.LabelField("REFERENCE PREFABS", CardHeaderStyle);

            var dropRect = GUILayoutUtility.GetRect(
                0, 54,
                GUILayout.ExpandWidth(true));

            var evt = Event.current;
            var hovering = dropRect.Contains(evt.mousePosition)
                && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform);

            DrawDropZoneFrame(dropRect, hovering, hasContent: _referencePrefabs.Count > 0);

            if (_referencePrefabs.Count == 0)
            {
                var titleStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = hovering ? AccentBlue : new Color(0.55f, 0.55f, 0.55f) },
                };
                GUI.Label(dropRect,
                    hovering ? "Release to add prefab" : "Drop button/layout prefabs here to reuse",
                    titleStyle);
            }
            else
            {
                var style = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    normal = { textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.2f, 0.2f, 0.2f) },
                };
                GUI.Label(dropRect, string.Join("   ",
                    _referencePrefabs.ConvertAll(p => Path.GetFileNameWithoutExtension(p))),
                    style);
            }

            if (dropRect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
                else if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var p = AssetDatabase.GetAssetPath(obj);
                        if (string.IsNullOrEmpty(p) || !p.EndsWith(".prefab")) continue;
                        if (_referencePrefabs.Contains(p)) continue;
                        _referencePrefabs.Add(p);
                    }
                    evt.Use();
                    Repaint();
                }
            }

            if (_referencePrefabs.Count > 0)
            {
                EditorGUILayout.Space(4);
                int removeIdx = -1;
                for (int i = 0; i < _referencePrefabs.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var pathStyle = new GUIStyle(EditorStyles.miniLabel)
                        {
                            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                        };
                        EditorGUILayout.LabelField(_referencePrefabs[i], pathStyle);
                        if (GUILayout.Button("×", GUILayout.Width(20), GUILayout.Height(16)))
                            removeIdx = i;
                    }
                }
                if (removeIdx >= 0) _referencePrefabs.RemoveAt(removeIdx);

                if (GUILayout.Button("Clear All", GUILayout.Width(80), GUILayout.Height(16)))
                    _referencePrefabs.Clear();
            }
        }

        private void DrawReferenceFont()
        {
            EditorGUILayout.LabelField("TMP FONT", CardHeaderStyle);

            using (new EditorGUILayout.HorizontalScope())
            {
                UnityEngine.Object current = null;
                if (!string.IsNullOrEmpty(_referenceFontPath))
                {
                    current = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(_referenceFontPath);
                }

                var picked = EditorGUILayout.ObjectField(current, typeof(TMPro.TMP_FontAsset), false);
                if (picked != current)
                {
                    _referenceFontPath = picked != null ? AssetDatabase.GetAssetPath(picked) : null;
                }

                GUI.enabled = !string.IsNullOrEmpty(_referenceFontPath);
                if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(18)))
                    _referenceFontPath = null;
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(_referenceFontPath))
            {
                var pathStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                };
                EditorGUILayout.LabelField(_referenceFontPath, pathStyle);
            }
        }

        private static void OpenLogsFolder()
        {
            Directory.CreateDirectory(LogRoot);
            EditorUtility.RevealInFinder(LogRoot);
        }

        private void DrawStatus()
        {
            // 승인 대기 중이면 섹션 라벨을 다르게 — 유저 주의 끌기
            if (!string.IsNullOrEmpty(_pendingApprovalTreeSpec))
            {
                DrawApprovalSectionHeader("⏸  STRUCTURE REVIEW  —  아래 TreeSpec 확인 후 INTENT 의 [Accept Structure]");
            }
            else if (_pendingFinalState != null)
            {
                DrawApprovalSectionHeader("✓  RESULT PREVIEW  —  만족하면 [Accept Prefab], 수정하려면 힌트 입력 후 [Refine]");
            }
            else
            {
                DrawSectionLabel("STATUS");
            }

            var boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
            };

            using (new EditorGUILayout.VerticalScope(boxStyle, GUILayout.MinHeight(200), GUILayout.ExpandHeight(true)))
            {
                if (!string.IsNullOrEmpty(_pendingApprovalTreeSpec))
                {
                    DrawApprovalEditor();
                    return;
                }

                if (_pendingFinalState != null)
                {
                    DrawFinalResultPanel();
                    return;
                }

                if (_isRunning)
                {
                    DrawRunningStatus();
                    return;
                }

                if (string.IsNullOrEmpty(_status))
                {
                    var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                    {
                        fontStyle = FontStyle.Italic,
                    };
                    EditorGUILayout.LabelField("아직 실행 결과가 없습니다.", hint);
                    return;
                }

                _statusScroll = EditorGUILayout.BeginScrollView(
                    _statusScroll,
                    GUILayout.MinHeight(72),
                    GUILayout.ExpandHeight(true));

                var bodyStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = true,
                };
                EditorGUILayout.LabelField(_status, bodyStyle);

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawFinalResultPanel()
        {
            var fs = _pendingFinalState;

            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal = { textColor = AccentGreen },
            };
            EditorGUILayout.LabelField($"✓ Reviewer 판정: OK (iter {fs.iterationsUsed})", titleStyle);

            if (!string.IsNullOrEmpty(fs.reviewerSummary))
            {
                var summaryStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    normal = { textColor = new Color(0.75f, 0.75f, 0.75f) },
                };
                EditorGUILayout.LabelField(fs.reviewerSummary, summaryStyle);
            }

            EditorGUILayout.Space(6);

            // 스크린샷 + Preview 버튼
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!string.IsNullOrEmpty(fs.screenshotPath) && File.Exists(fs.screenshotPath))
                {
                    var bytes = File.ReadAllBytes(fs.screenshotPath);
                    var tex = new Texture2D(2, 2);
                    tex.LoadImage(bytes);
                    var rect = GUILayoutUtility.GetRect(200, 320, GUILayout.Width(200), GUILayout.Height(320));
                    GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    var pathStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        wordWrap = true,
                        normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                    };
                    EditorGUILayout.LabelField("Prefab", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(fs.prefabPath, pathStyle);

                    EditorGUILayout.Space(6);
                    if (GUILayout.Button("👁  Preview Layout (structure)", GUILayout.Height(24)))
                    {
                        PrefabTreePreviewWindow.ShowFor(fs.treeSpecJson, OnPreviewNodeClicked);
                    }
                    if (GUILayout.Button("🔍  Open Prefab in Project", GUILayout.Height(22)))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(fs.prefabPath);
                        if (asset != null) EditorGUIUtility.PingObject(asset);
                    }
                }
            }

            EditorGUILayout.Space(6);
            var helpStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };
            EditorGUILayout.LabelField(
                "수정 지시가 있으면 위 INTENT 텍스트영역에 입력 후 [Refine]. Preview 에서 노드를 클릭하면 힌트에 참조가 자동 삽입됨. 만족하면 [Accept Prefab].",
                helpStyle);
        }

        private void DrawApprovalSectionHeader(string msg)
        {
            var r = EditorGUILayout.GetControlRect(false, 24);
            EditorGUI.DrawRect(r, new Color(AccentBlue.r, AccentBlue.g, AccentBlue.b, 0.18f));
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal = { textColor = AccentBlue },
                padding = new RectOffset(8, 8, 4, 4),
            };
            GUI.Label(r, msg, style);
        }

        private void OnPreviewNodeClicked(string nodeName)
        {
            if (string.IsNullOrEmpty(nodeName)) return;
            var token = "@" + nodeName + " ";
            if (string.IsNullOrEmpty(_userHint)) _userHint = token;
            else if (!_userHint.EndsWith(" ")) _userHint = _userHint + " " + token;
            else _userHint = _userHint + token;
            Focus();
            Repaint();
        }

        private void DrawApprovalEditor()
        {
            var hint = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };
            EditorGUILayout.LabelField(
                "Analyzer 가 만든 TreeSpec 입니다. 구조 확인 후 [Continue ▶] 를 누르거나, JSON 을 수정해 의도 교정 후 [Continue ▶]. Reviewer 가 이후 크기를 미세조정합니다.",
                hint);

            EditorGUILayout.Space(4);

            // 탭 전환: [Preview] [JSON] — 기본 Preview 표시, JSON 은 고급 수정용
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("👁  Preview Layout (new window)", EditorStyles.miniButton, GUILayout.Height(22)))
                {
                    PrefabTreePreviewWindow.ShowFor(_pendingApprovalTreeSpec, OnPreviewNodeClicked);
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4);

            _treeSpecScroll = EditorGUILayout.BeginScrollView(
                _treeSpecScroll,
                GUILayout.MinHeight(180),
                GUILayout.ExpandHeight(true));

            var editorStyle = new GUIStyle(EditorStyles.textArea)
            {
                font = EditorStyles.miniFont,
                wordWrap = false,
                richText = false,
            };
            _pendingApprovalTreeSpec = EditorGUILayout.TextArea(
                _pendingApprovalTreeSpec,
                editorStyle,
                GUILayout.ExpandHeight(true));

            EditorGUILayout.EndScrollView();

            // 버튼은 INTENT 카드로 이관됨 — 여기엔 안내만
            var hintNote = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };
            EditorGUILayout.LabelField("→ 상단 INTENT 카드의 [Accept Structure] 로 진행하거나, Preview 에서 노드를 클릭해 힌트로 추가", hintNote);
        }

        private void DrawRunningStatus()
        {
            var dots = new string('.', _thinkingDots);
            var runningStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = AccentBlue },
            };
            var subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
            };

            if (_isMyJobRunning)
            {
                var elapsed = EditorApplication.timeSinceStartup - _runStartedAt;
                EditorGUILayout.LabelField($"Running{dots}", runningStyle);
                EditorGUILayout.LabelField($"경과 {elapsed:F0}s", subStyle);
            }
            else
            {
                var q = UniMcpRunQueue.QueuedCount;
                var running = UniMcpRunQueue.RunningSkill;
                EditorGUILayout.LabelField($"Queued{dots}", runningStyle);
                var detail = running != null
                    ? $"앞선 작업 실행 중: {running.name} · 대기: {q}"
                    : $"대기: {q}";
                EditorGUILayout.LabelField(detail, subStyle);
            }
        }

        private static void DrawSectionLabel(string text)
        {
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                fontSize = 10,
                normal = { textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.62f, 0.62f, 0.62f)
                    : new Color(0.38f, 0.38f, 0.38f) },
            };
            EditorGUILayout.LabelField(text, style);
            EditorGUILayout.Space(2);
        }

        private static void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, Separator);
        }

        private static void DrawDropZoneFrame(Rect rect, bool hovering, bool hasContent)
        {
            var bg = hovering
                ? new Color(AccentBlue.r, AccentBlue.g, AccentBlue.b, 0.10f)
                : PanelTint;
            EditorGUI.DrawRect(rect, bg);

            var border = hovering
                ? AccentBlue
                : (hasContent
                    ? new Color(AccentBlue.r, AccentBlue.g, AccentBlue.b, 0.35f)
                    : Separator);
            DrawBorder(rect, border, 1f);
        }

        private static void DrawDropZoneHint(Rect rect, bool hovering)
        {
            var titleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = hovering
                    ? AccentBlue
                    : (EditorGUIUtility.isProSkin
                        ? new Color(0.70f, 0.70f, 0.70f)
                        : new Color(0.40f, 0.40f, 0.40f)) },
            };
            var subStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };

            var half = rect.height * 0.5f;
            var top = new Rect(rect.x, rect.y + half - 20, rect.width, 20);
            var bot = new Rect(rect.x, rect.y + half + 2, rect.width, 16);

            GUI.Label(top, hovering ? "Release to set image" : "Drop image here", titleStyle);
            GUI.Label(bot, ".png / .jpg / .psd / .tga / ...", subStyle);
        }

        private static void DrawBorder(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private PrefabHook.PrefabGenerationPipeline _pipeline;
        private string _pendingApprovalTreeSpec;
        private PrefabHook.PrefabGenerationPipeline.FinalState _pendingFinalState;
        private Vector2 _treeSpecScroll;

        private void TriggerRun()
        {
            if (string.IsNullOrEmpty(_inputImagePath) || _isRunning)
                return;

            var imagePath = _inputImagePath;

            Directory.CreateDirectory(ManifestRoot);
            Directory.CreateDirectory(LogRoot);
            var jobId = Guid.NewGuid();
            var manifestPath = Path.Combine(ManifestRoot, jobId + ".json").Replace('\\', '/');
            var shortId = jobId.ToString().Substring(0, 8);
            var logPath = Path.Combine(LogRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}_{shortId}.log").Replace('\\', '/');
            _currentLogPath = logPath;
            WriteInitialManifest(manifestPath, jobId, imagePath, _outputFolder, _userHint, _referencePrefabs, _referenceFontPath);

            _currentManifestPath = manifestPath;
            _myQueuedSkill = BuiltinSkills.GetPrefabGenSkill();
            _isMyJobRunning = false;
            _isRunning = true;
            _runStartedAt = EditorApplication.timeSinceStartup;
            _thinkingDots = 0;
            _status = "파이프라인 시작…";

            _pipeline = new PrefabHook.PrefabGenerationPipeline(
                imagePath, manifestPath, logPath, shortId,
                onStatus: s =>
                {
                    _status = s;
                    Repaint();
                },
                onComplete: result =>
                {
                    _status = (result.success ? "완료" : "실패") +
                              $" (iter {result.iterationsUsed}): " +
                              (result.summary ?? result.error ?? "");
                    _pendingApprovalTreeSpec = null;
                    _pendingFinalState = null;
                    if (result.success) _userHint = "";   // 제출 intent 는 완료되면 비움
                    OnMyJobDone();
                });
            // Structure Review 단계 기본 스킵 (Analyzer → 자동 Generator). 단일 Accept 게이트 = 최종 수락만
            // _pipeline.OnTreeSpecReady = null; (설정 안 함)
            _pipeline.OnFinalReady = fs =>
            {
                _pendingFinalState = fs;
                Repaint();
            };
            _pipeline.Start();
            _currentJobId = _pipeline.CurrentJobId;

            if (_currentJobId == Guid.Empty)
            {
                _status = "파이프라인 시작 실패.";
                TryCleanupManifest(manifestPath);
                OnMyJobDone();
                return;
            }

            Repaint();
        }

        private void TriggerStop()
        {
            if (!_isRunning)
                return;

            try { _pipeline?.Cancel(); }
            catch (Exception e) { UniMcpLogger.Warn("Pipeline Cancel 실패: " + e.Message); }

            if (_currentJobId != Guid.Empty)
            {
                try { UniMcpRunQueue.Cancel(_currentJobId); }
                catch (Exception e) { UniMcpLogger.Warn("Cancel 실패: " + e.Message); }
            }

            // Stop 은 프리팹 **보존** — 유저 작업물 삭제하지 않음. manifest 만 정리
            TryCleanupManifest(_currentManifestPath);

            _status = "중단됨 (프리팹 유지)";
            OnMyJobDone();
            AssetDatabase.Refresh();
        }

        private void OnMyJobDone()
        {
            _isRunning = false;
            _isMyJobRunning = false;
            _myQueuedSkill = null;
            _currentJobId = Guid.Empty;
            _currentManifestPath = null;
            _lastRunAt = DateTime.Now.ToString("HH:mm:ss");
            Repaint();
        }

        [Serializable]
        private class Manifest
        {
            public string jobId;
            public string imagePath;
            public string outputFolder;
            public string userHint;
            public PrefabHook.PrefabMetadataExtractor.PrefabMeta[] referencePrefabs =
                Array.Empty<PrefabHook.PrefabMetadataExtractor.PrefabMeta>();
            public string referenceFont;
            public string[] createdAssets = Array.Empty<string>();
        }

        private static void WriteInitialManifest(
            string manifestPath, Guid jobId, string imagePath, string outputFolder,
            string userHint, List<string> referencePrefabs, string referenceFont)
        {
            var prefabMetas = PrefabHook.PrefabMetadataExtractor.ExtractAll(referencePrefabs);

            var manifest = new Manifest
            {
                jobId = jobId.ToString(),
                imagePath = imagePath,
                outputFolder = outputFolder ?? "",
                userHint = userHint ?? "",
                referencePrefabs = prefabMetas,
                referenceFont = referenceFont ?? "",
                createdAssets = Array.Empty<string>(),
            };
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, prettyPrint: true));
        }

        // ProcessAnalyzerResult / AppendCreatedAsset / AppendToLog 는 PrefabGenerationPipeline 로 이관됨

        /// <summary>
        /// manifest 의 createdAssets 에 적힌 경로들을 전부 삭제.
        /// 삭제된 개수 반환. manifest 없으면 0
        /// </summary>
        private static int DeleteArtifactsFromManifest(string manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return 0;

            Manifest manifest;
            try { manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath)); }
            catch (Exception e)
            {
                UniMcpLogger.Warn("Manifest 파싱 실패: " + e.Message);
                return 0;
            }

            if (manifest?.createdAssets == null) return 0;

            int deleted = 0;
            foreach (var rel in manifest.createdAssets)
            {
                if (string.IsNullOrEmpty(rel)) continue;

                // Assets 하위 경로만 허용 — 엉뚱한 경로 삭제 방지
                var norm = rel.Replace('\\', '/');
                if (!norm.StartsWith("Assets/")) continue;

                try
                {
                    if (AssetDatabase.DeleteAsset(norm))
                        deleted++;
                }
                catch (Exception e)
                {
                    UniMcpLogger.Warn($"에셋 삭제 실패 ({norm}): " + e.Message);
                }
            }

            return deleted;
        }

        private static void TryCleanupManifest(string manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath)) return;
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
            catch { }
        }

        private static Rect FitAspect(Rect container, float aspect)
        {
            var cw = container.width;
            var ch = container.height;
            var rw = cw;
            var rh = cw / Mathf.Max(0.0001f, aspect);
            if (rh > ch)
            {
                rh = ch;
                rw = ch * aspect;
            }
            return new Rect(
                container.x + (cw - rw) * 0.5f,
                container.y + (ch - rh) * 0.5f,
                rw, rh);
        }
    }
}
