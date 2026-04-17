using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSkillExecutorWindow : EditorWindow
    {
        [SerializeField] private List<UnityEngine.Object> _targets = new();
        [SerializeField] private int _selectedSkillIdx;
        [SerializeField] private string _result;
        [SerializeField] private string _lastRunAt;

        private bool _isRunning;
        private double _runStartedAt;
        private int _thinkingDots;
        private Vector2 _resultScroll;
        private Vector2 _targetsScroll;

        [MenuItem("UniMCP/Skill Executor")]
        private static void Open()
        {
            var existing = DockUtil.FindFirstOpen<UniMcpSkillExecutorWindow>();
            if (existing != null)
            {
                existing.Focus();
                return;
            }

            var anchor = (EditorWindow)DockUtil.FindFirstOpen<UniMcpWindow>()
                      ?? DockUtil.FindFirstOpen<UniMcpSettingsWindow>();

            if (anchor == null)
            {
                var w = GetWindow<UniMcpSkillExecutorWindow>("Skill Executor");
                w.minSize = new Vector2(520, 480);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSkillExecutorWindow>();
            window.titleContent = new GUIContent("Skill Executor");
            window.minSize = new Vector2(520, 480);

            if (!DockUtil.TryDockNextTo(anchor, window))
                window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
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

        private void OnGUI()
        {
            DrawHeader();
            DrawSkillPicker();
            EditorGUILayout.Space(6);
            DrawTargets();
            EditorGUILayout.Space(6);
            DrawRunBar();
            EditorGUILayout.Space(6);
            DrawResult();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Skill Executor", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "선택한 스킬을 첨부 대상(프리팹·스크립트)에 대해 실행합니다.",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);
        }

        private void DrawSkillPicker()
        {
            var skills = UniMcpSettings.instance.Skills;

            if (skills.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "정의된 스킬이 없습니다. UniMCP → Settings → Skills 에서 먼저 스킬을 만드세요.",
                    MessageType.Warning);
                return;
            }

            var names = skills.Select(s => s.name).ToArray();
            _selectedSkillIdx = Mathf.Clamp(_selectedSkillIdx, 0, names.Length - 1);
            _selectedSkillIdx = EditorGUILayout.Popup("Skill", _selectedSkillIdx, names);
        }

        private void DrawTargets()
        {
            EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);

            var dropRect = GUILayoutUtility.GetRect(
                0, 56,
                GUILayout.ExpandWidth(true));

            var evt = Event.current;
            var hovering = dropRect.Contains(evt.mousePosition)
                && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform);

            var dropStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Italic,
            };
            if (hovering)
                dropStyle.normal.textColor = new Color(0.4f, 0.8f, 1f);

            GUI.Box(
                dropRect,
                hovering ? "Release to add" : "Drop prefabs / scripts here",
                dropStyle);

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
                        var resolved = ResolveToAsset(obj);
                        if (resolved == null) continue;
                        if (_targets.Contains(resolved)) continue;
                        _targets.Add(resolved);
                    }
                    evt.Use();
                    Repaint();
                }
            }

            if (_targets.Count == 0)
            {
                EditorGUILayout.HelpBox("첨부된 대상이 없습니다.", MessageType.Info);
                DrawAddFromSelectionButton();
                return;
            }

            _targetsScroll = EditorGUILayout.BeginScrollView(
                _targetsScroll,
                GUILayout.MinHeight(80),
                GUILayout.MaxHeight(160));

            int pendingRemove = -1;
            for (int i = 0; i < _targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField(_targets[i], typeof(UnityEngine.Object), false);
                    if (GUILayout.Button("×", GUILayout.Width(24)))
                        pendingRemove = i;
                }
            }

            EditorGUILayout.EndScrollView();

            if (pendingRemove >= 0)
                _targets.RemoveAt(pendingRemove);

            DrawAddFromSelectionButton();
        }

        private void DrawAddFromSelectionButton()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Add from Selection", GUILayout.Width(180)))
                    AddFromSelection();

                GUILayout.FlexibleSpace();

                GUI.enabled = _targets.Count > 0;
                if (GUILayout.Button("Clear", GUILayout.Width(80)))
                    _targets.Clear();
                GUI.enabled = true;
            }
        }

        private void AddFromSelection()
        {
            foreach (var o in Selection.objects)
            {
                var resolved = ResolveToAsset(o);
                if (resolved == null)
                    continue;
                if (_targets.Contains(resolved))
                    continue;
                _targets.Add(resolved);
            }
        }

        private static UnityEngine.Object ResolveToAsset(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)))
                return obj;

            if (obj is GameObject go)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                if (source != null)
                {
                    var path = AssetDatabase.GetAssetPath(source);
                    if (!string.IsNullOrEmpty(path))
                        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
                }
            }

            return null;
        }

        private void DrawRunBar()
        {
            var skills = UniMcpSettings.instance.Skills;
            var canRun = !_isRunning
                && skills.Count > 0
                && _targets.Count > 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = canRun;
                if (GUILayout.Button(_isRunning ? "..." : "Run", GUILayout.Height(32)))
                    _ = RunAsync();
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(_lastRunAt))
                EditorGUILayout.LabelField($"Last run: {_lastRunAt}", EditorStyles.miniLabel);
        }

        private void DrawResult()
        {
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_isRunning)
                {
                    var elapsed = EditorApplication.timeSinceStartup - _runStartedAt;
                    var dots = new string('.', _thinkingDots);
                    var thinkingStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontStyle = FontStyle.Italic,
                        normal = { textColor = new Color(0.70f, 0.70f, 0.70f) },
                    };
                    EditorGUILayout.LabelField($"Running{dots}  ({elapsed:F0}s)", thinkingStyle);
                    EditorGUILayout.LabelField(
                        "프리팹 자식 트리 전체를 스캔·리네임할 경우 30초~수 분 걸릴 수 있습니다.",
                        EditorStyles.miniLabel);
                    return;
                }

                if (string.IsNullOrEmpty(_result))
                {
                    var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
                    EditorGUILayout.LabelField("아직 실행 결과가 없습니다.", hint);
                    return;
                }

                _resultScroll = EditorGUILayout.BeginScrollView(
                    _resultScroll,
                    GUILayout.ExpandHeight(true));

                var bodyStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = true,
                };
                EditorGUILayout.LabelField(MarkdownRenderer.ToRichText(_result), bodyStyle);

                EditorGUILayout.EndScrollView();
            }
        }

        private async Task RunAsync()
        {
            var skills = UniMcpSettings.instance.Skills;
            if (skills.Count == 0 || _targets.Count == 0)
                return;

            var skill = skills[Mathf.Clamp(_selectedSkillIdx, 0, skills.Count - 1)];
            var prompt = BuildPrompt(skill.name);

            _isRunning = true;
            _runStartedAt = EditorApplication.timeSinceStartup;
            _thinkingDots = 0;
            _result = "";
            Repaint();

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var response = await ClaudeProcess.Send(prompt, projectRoot, null);
                _result = response.result ?? "";
            }
            catch (Exception e)
            {
                _result = "Error: " + e.Message;
            }
            finally
            {
                _isRunning = false;
                _lastRunAt = DateTime.Now.ToString("HH:mm:ss");
                Repaint();
            }
        }

        private string BuildPrompt(string skillName)
        {
            var paths = new List<string>();
            foreach (var t in _targets)
            {
                var path = AssetDatabase.GetAssetPath(t);
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
            var invocation = SkillStore.GetInvocationName(skillName);
            return $"/{invocation} Targets: {string.Join(", ", paths)}";
        }
    }
}
