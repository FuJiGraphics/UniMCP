using System;
using System.Collections.Generic;
using System.Linq;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSkillGeneratorWindow : EditorWindow
    {
        private static readonly Color ColorAccent         = new(0.40f, 0.80f, 1.00f);
        private static readonly Color ColorDirty          = new(1.00f, 0.80f, 0.20f);
        private static readonly Color ColorDanger         = new(0.95f, 0.35f, 0.35f);
        private static readonly Color ColorMuted          = new(0.60f, 0.60f, 0.60f);
        private static readonly Color ColorBgCard         = new(0.18f, 0.18f, 0.20f);
        private static readonly Color ColorBgCardSelected = new(0.14f, 0.20f, 0.30f);
        private static readonly Color ColorSeparator      = new(0f, 0f, 0f, 0.3f);

        private const float SidebarWidth = 260f;
        private const float CardHeight   = 46f;

        [SerializeField] private List<UniMcpSkill> _skillsBuffer;
        [SerializeField] private List<UniMcpSkill> _skillsSnapshot;
        [SerializeField] private int _selectedSkillIdx = -1;
        [SerializeField] private Vector2 _listScroll;
        [SerializeField] private Vector2 _editorScroll;
        [SerializeField] private Vector2 _previewScroll;
        [SerializeField] private string _filter = "";
        [SerializeField] private bool _showPreview;

        [MenuItem("UniMCP/Skill Generator")]
        private static void Open()
        {
            var existing = DockUtil.FindFirstOpen<UniMcpSkillGeneratorWindow>();
            if (existing != null)
            {
                existing.Focus();
                return;
            }

            var anchor = (EditorWindow)DockUtil.FindFirstOpen<UniMcpWindow>()
                      ?? DockUtil.FindFirstOpen<UniMcpSettingsWindow>()
                      ?? DockUtil.FindFirstOpen<UniMcpSkillExecutorWindow>();

            if (anchor == null)
            {
                var w = GetWindow<UniMcpSkillGeneratorWindow>("Skill Generator");
                w.minSize = new Vector2(720, 540);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSkillGeneratorWindow>();
            window.titleContent = new GUIContent("Skill Generator");
            window.minSize = new Vector2(720, 540);

            if (!DockUtil.TryDockNextTo(anchor, window))
                window.Show();
        }

        private void OnEnable()
        {
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            var s = UniMcpSettings.instance;
            _skillsBuffer = s.Skills.Select(x => x.Clone()).ToList();
            _skillsSnapshot = s.Skills.Select(x => x.Clone()).ToList();

            if (_skillsBuffer.Count == 0)
                _selectedSkillIdx = -1;
            else if (_selectedSkillIdx < 0 || _selectedSkillIdx >= _skillsBuffer.Count)
                _selectedSkillIdx = 0;
        }

        private void OnGUI()
        {
            DrawToolbar();

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                DrawSidebar();
                DrawVerticalSeparator();
                DrawDetail();
            }

            DrawStatusBar();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("🔍", GUILayout.Width(18));
                _filter = EditorGUILayout.TextField(
                    _filter,
                    EditorStyles.toolbarSearchField,
                    GUILayout.MinWidth(160));

                if (!string.IsNullOrEmpty(_filter)
                    && GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    _filter = "";
                }

                GUILayout.FlexibleSpace();

                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = ColorAccent;
                if (GUILayout.Button("+ New Skill", EditorStyles.toolbarButton, GUILayout.Width(100)))
                    AddNewSkill();
                GUI.backgroundColor = prevBg;
            }
        }

        private void DrawSidebar()
        {
            using (new EditorGUILayout.VerticalScope(
                       GUILayout.Width(SidebarWidth),
                       GUILayout.ExpandHeight(true)))
            {
                var filtered = GetFilteredIndices();

                if (filtered.Count == 0)
                {
                    EditorGUILayout.Space(12);
                    var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                    var msg = _skillsBuffer.Count == 0
                        ? "스킬 없음"
                        : $"'{_filter}'와 일치하는 스킬 없음";
                    EditorGUILayout.LabelField(msg, style);
                    GUILayout.FlexibleSpace();
                    return;
                }

                _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));

                int pendingRemove = -1;
                foreach (var i in filtered)
                {
                    if (DrawSkillCard(i))
                        pendingRemove = i;
                }

                EditorGUILayout.EndScrollView();

                if (pendingRemove >= 0)
                    RemoveSkill(pendingRemove);
            }
        }

        private bool DrawSkillCard(int i)
        {
            var skill = _skillsBuffer[i];
            var selected = i == _selectedSkillIdx;
            var dirty = IsSkillDirty(i);

            var rect = GUILayoutUtility.GetRect(0, CardHeight, GUILayout.ExpandWidth(true));
            var inner = new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4);

            EditorGUI.DrawRect(inner, selected ? ColorBgCardSelected : ColorBgCard);

            if (selected)
                EditorGUI.DrawRect(new Rect(inner.x, inner.y, 4, inner.height), ColorAccent);

            var xRect = new Rect(inner.x + inner.width - 22, inner.y + 4, 20, 20);
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = ColorDanger * new Color(1, 1, 1, 0.7f);
            bool removed = GUI.Button(xRect, "×", EditorStyles.miniButton);
            GUI.backgroundColor = prevBg;

            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && inner.Contains(Event.current.mousePosition)
                && !xRect.Contains(Event.current.mousePosition))
            {
                _selectedSkillIdx = i;
                GUI.FocusControl(null);
                Repaint();
            }

            var nameStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = selected ? Color.white : Color.white * 0.92f },
            };
            var nameLabel = string.IsNullOrWhiteSpace(skill.name) ? "(no name)" : skill.name;
            GUI.Label(new Rect(inner.x + 10, inner.y + 4, inner.width - 60, 18), nameLabel, nameStyle);

            if (dirty)
            {
                var dotRect = new Rect(inner.x + inner.width - 36, inner.y + 9, 8, 8);
                EditorGUI.DrawRect(dotRect, ColorDirty);
            }

            var previewStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = ColorMuted },
                alignment = TextAnchor.MiddleLeft,
            };
            GUI.Label(
                new Rect(inner.x + 10, inner.y + 24, inner.width - 60, 18),
                GetPreviewText(skill.prompt),
                previewStyle);

            return removed;
        }

        private static string GetPreviewText(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                return "(empty)";

            foreach (var line in prompt.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t))
                    continue;
                while (t.StartsWith("#"))
                    t = t.Substring(1).TrimStart();
                return t.Length <= 60 ? t : t.Substring(0, 57) + "…";
            }
            return "(empty)";
        }

        private void DrawVerticalSeparator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.Width(1), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(rect, ColorSeparator);
        }

        private void DrawDetail()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                if (_skillsBuffer.Count == 0)
                {
                    DrawEmptyState();
                    return;
                }

                if (_selectedSkillIdx < 0 || _selectedSkillIdx >= _skillsBuffer.Count)
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        "왼쪽에서 스킬을 선택하세요.",
                        new GUIStyle(EditorStyles.centeredGreyMiniLabel));
                    GUILayout.FlexibleSpace();
                    return;
                }

                DrawEditor();
            }
        }

        private void DrawEditor()
        {
            var skill = _skillsBuffer[_selectedSkillIdx];

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Name", GUILayout.Width(50));
                var newName = EditorGUILayout.TextField(skill.name);
                if (newName != skill.name)
                {
                    var dupe = _skillsBuffer
                        .Where((s, idx) => idx != _selectedSkillIdx)
                        .Any(s => (s.name ?? "").Trim() == (newName ?? "").Trim());
                    if (!dupe)
                        skill.name = newName;
                }
            }

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Prompt (markdown)", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                _showPreview = GUILayout.Toggle(
                    _showPreview,
                    _showPreview ? "Preview ✓" : "Preview",
                    EditorStyles.miniButton,
                    GUILayout.Width(90));
            }

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
                {
                    _editorScroll = EditorGUILayout.BeginScrollView(
                        _editorScroll,
                        GUILayout.ExpandHeight(true),
                        GUILayout.MinHeight(260));

                    var textAreaStyle = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = true,
                        richText = false,
                    };
                    skill.prompt = EditorGUILayout.TextArea(
                        skill.prompt ?? "",
                        textAreaStyle,
                        GUILayout.ExpandHeight(true));

                    EditorGUILayout.EndScrollView();
                }

                if (_showPreview)
                {
                    using (new EditorGUILayout.VerticalScope(
                               EditorStyles.helpBox,
                               GUILayout.ExpandHeight(true),
                               GUILayout.Width(position.width * 0.4f)))
                    {
                        _previewScroll = EditorGUILayout.BeginScrollView(
                            _previewScroll,
                            GUILayout.ExpandHeight(true));

                        var rendered = MarkdownRenderer.ToRichText(skill.prompt ?? "");
                        var style = new GUIStyle(EditorStyles.label)
                        {
                            wordWrap = true,
                            richText = true,
                        };
                        EditorGUILayout.LabelField(rendered, style);

                        EditorGUILayout.EndScrollView();
                    }
                }
            }

            var (chars, lines) = CountCharsLines(skill.prompt);
            EditorGUILayout.LabelField(
                $"{chars:N0} chars · {lines} lines",
                EditorStyles.miniLabel);
        }

        private static (int chars, int lines) CountCharsLines(string s)
        {
            if (string.IsNullOrEmpty(s))
                return (0, 0);

            int lines = 1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n')
                    lines++;
            return (s.Length, lines);
        }

        private void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(320)))
                {
                    var iconStyle = new GUIStyle(EditorStyles.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 42,
                    };
                    GUILayout.Label("✨", iconStyle, GUILayout.Height(60));

                    var titleStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 14,
                    };
                    EditorGUILayout.LabelField("아직 스킬이 없습니다", titleStyle);

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(
                        "첫 스킬을 만들어 프로젝트 전용 작업을 자동화하세요.",
                        new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });

                    EditorGUILayout.Space(14);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = ColorAccent;
                        if (GUILayout.Button("+ Create First Skill",
                                GUILayout.Width(200), GUILayout.Height(30)))
                            AddNewSkill();
                        GUI.backgroundColor = prev;
                        GUILayout.FlexibleSpace();
                    }
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.FlexibleSpace();
        }

        private void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var count = _skillsBuffer.Count;
                GUILayout.Label($"{count} skill{(count == 1 ? "" : "s")}", EditorStyles.miniLabel);

                if (HasUnsavedChanges())
                {
                    var dirtyStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = ColorDirty },
                    };
                    GUILayout.Label(" ● unsaved", dirtyStyle);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    LoadFromSettings();

                var canSave = HasUnsavedChanges();
                GUI.enabled = canSave;
                var savePrevBg = GUI.backgroundColor;
                if (canSave)
                    GUI.backgroundColor = ColorAccent;
                if (GUILayout.Button(canSave ? "Save *" : "Save",
                        EditorStyles.toolbarButton, GUILayout.Width(80)))
                    SaveAll();
                GUI.backgroundColor = savePrevBg;
                GUI.enabled = true;
            }
        }

        private List<int> GetFilteredIndices()
        {
            var result = new List<int>();
            var f = (_filter ?? "").Trim();
            for (int i = 0; i < _skillsBuffer.Count; i++)
            {
                if (string.IsNullOrEmpty(f)
                    || (_skillsBuffer[i].name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(i);
                }
            }
            return result;
        }

        private void AddNewSkill()
        {
            int n = _skillsBuffer.Count + 1;
            string name;
            while (true)
            {
                name = $"Skill {n}";
                if (!_skillsBuffer.Any(s => (s.name ?? "").Trim() == name))
                    break;
                n++;
            }

            _skillsBuffer.Add(new UniMcpSkill { name = name, prompt = "" });
            _selectedSkillIdx = _skillsBuffer.Count - 1;
            _filter = "";
            _editorScroll = Vector2.zero;
            GUI.FocusControl(null);
            Repaint();
        }

        private void RemoveSkill(int index)
        {
            _skillsBuffer.RemoveAt(index);
            if (_skillsBuffer.Count == 0)
                _selectedSkillIdx = -1;
            else if (_selectedSkillIdx >= _skillsBuffer.Count)
                _selectedSkillIdx = _skillsBuffer.Count - 1;
            else if (index < _selectedSkillIdx)
                _selectedSkillIdx--;
            Repaint();
        }

        private bool IsSkillDirty(int i)
        {
            if (_skillsSnapshot == null || i >= _skillsSnapshot.Count)
                return true;

            var a = _skillsBuffer[i];
            var b = _skillsSnapshot[i];
            return a.name != b.name || a.prompt != b.prompt;
        }

        private bool HasUnsavedChanges()
        {
            if (_skillsBuffer == null || _skillsSnapshot == null)
                return false;
            if (_skillsBuffer.Count != _skillsSnapshot.Count)
                return true;
            for (int i = 0; i < _skillsBuffer.Count; i++)
                if (IsSkillDirty(i))
                    return true;
            return false;
        }

        private void SaveAll()
        {
            var names = _skillsBuffer.Select(s => (s.name ?? "").Trim()).ToList();

            if (names.Any(string.IsNullOrEmpty))
            {
                EditorUtility.DisplayDialog(
                    "Invalid Skills",
                    "이름이 비어 있는 스킬이 있습니다.",
                    "OK");
                return;
            }

            if (names.Distinct().Count() != names.Count)
            {
                EditorUtility.DisplayDialog(
                    "Duplicate Names",
                    "스킬 이름이 중복됩니다.",
                    "OK");
                return;
            }

            SkillStore.Sync(_skillsSnapshot, _skillsBuffer);
            UniMcpSettings.instance.SetSkills(_skillsBuffer);
            _skillsSnapshot = _skillsBuffer.Select(s => s.Clone()).ToList();
        }
    }
}
