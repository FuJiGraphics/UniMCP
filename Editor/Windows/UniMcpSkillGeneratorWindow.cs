using System.Collections.Generic;
using System.Linq;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSkillGeneratorWindow : EditorWindow
    {
        private Vector2 _skillPromptScroll;
        private List<UniMcpSkill> _skillsBuffer;
        private List<UniMcpSkill> _skillsSnapshot;
        private int _selectedSkillIdx;
        private string _newSkillName = "";

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
                w.minSize = new Vector2(520, 480);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSkillGeneratorWindow>();
            window.titleContent = new GUIContent("Skill Generator");
            window.minSize = new Vector2(520, 480);

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
            _selectedSkillIdx = 0;
            _newSkillName = "";
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawSkillList();
            EditorGUILayout.Space(4);
            DrawSkillAddRow();
            EditorGUILayout.Space(8);
            DrawSkillEditor();
            EditorGUILayout.Space(8);
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Skill Generator", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "프로젝트 전용 스킬을 만들고 수정합니다. Save 시 .claude/skills/<name>/SKILL.md로 동기화되어 Claude가 자동 인식합니다. 챗에서 /<name>으로 호출.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);
        }

        private void DrawSkillList()
        {
            if (_skillsBuffer.Count == 0)
            {
                EditorGUILayout.HelpBox("스킬이 없습니다. 아래에서 이름을 입력해 추가하세요.", MessageType.Info);
                return;
            }

            int pendingRemove = -1;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                for (int i = 0; i < _skillsBuffer.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var selected = i == _selectedSkillIdx;
                        var btnStyle = new GUIStyle(EditorStyles.label)
                        {
                            fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                            alignment = TextAnchor.MiddleLeft,
                        };
                        var label = string.IsNullOrWhiteSpace(_skillsBuffer[i].name)
                            ? "(no name)"
                            : _skillsBuffer[i].name;

                        if (GUILayout.Button(label, btnStyle))
                            _selectedSkillIdx = i;

                        if (GUILayout.Button("×", GUILayout.Width(24)))
                            pendingRemove = i;
                    }
                }
            }

            if (pendingRemove >= 0)
            {
                _skillsBuffer.RemoveAt(pendingRemove);
                if (_selectedSkillIdx >= _skillsBuffer.Count)
                    _selectedSkillIdx = Mathf.Max(0, _skillsBuffer.Count - 1);
            }
        }

        private void DrawSkillAddRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _newSkillName = EditorGUILayout.TextField("New Skill Name", _newSkillName);

                var trimmed = (_newSkillName ?? "").Trim();
                var duplicate = !string.IsNullOrEmpty(trimmed)
                    && _skillsBuffer.Any(s => (s.name ?? "").Trim() == trimmed);
                var canAdd = !string.IsNullOrEmpty(trimmed) && !duplicate;

                GUI.enabled = canAdd;
                if (GUILayout.Button("+ Add", GUILayout.Width(80)))
                {
                    _skillsBuffer.Add(new UniMcpSkill { name = trimmed, prompt = "" });
                    _selectedSkillIdx = _skillsBuffer.Count - 1;
                    _newSkillName = "";
                }
                GUI.enabled = true;
            }

            if (!string.IsNullOrWhiteSpace(_newSkillName)
                && _skillsBuffer.Any(s => (s.name ?? "").Trim() == _newSkillName.Trim()))
            {
                EditorGUILayout.HelpBox(
                    $"'{_newSkillName.Trim()}' 이름이 이미 존재합니다.",
                    MessageType.Warning);
            }
        }

        private void DrawSkillEditor()
        {
            if (_skillsBuffer.Count == 0
                || _selectedSkillIdx < 0
                || _selectedSkillIdx >= _skillsBuffer.Count)
            {
                return;
            }

            var skill = _skillsBuffer[_selectedSkillIdx];

            EditorGUILayout.LabelField("Selected Skill", EditorStyles.boldLabel);

            var newName = EditorGUILayout.TextField("Name", skill.name);
            if (newName != skill.name)
            {
                var dupe = _skillsBuffer
                    .Where((s, idx) => idx != _selectedSkillIdx)
                    .Any(s => (s.name ?? "").Trim() == (newName ?? "").Trim());
                if (!dupe)
                    skill.name = newName;
            }

            EditorGUILayout.LabelField("Prompt (markdown)");
            _skillPromptScroll = EditorGUILayout.BeginScrollView(
                _skillPromptScroll,
                GUILayout.ExpandHeight(true),
                GUILayout.MinHeight(240));
            skill.prompt = EditorGUILayout.TextArea(
                skill.prompt ?? "",
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload", GUILayout.Width(80)))
                    LoadFromSettings();

                GUILayout.FlexibleSpace();

                var dirty = HasUnsavedChanges();
                GUI.enabled = dirty;
                if (GUILayout.Button(dirty ? "Save *" : "Save", GUILayout.Width(100)))
                    SaveAll();
                GUI.enabled = true;
            }
        }

        private bool HasUnsavedChanges()
        {
            if (_skillsBuffer.Count != _skillsSnapshot.Count)
                return true;

            for (int i = 0; i < _skillsBuffer.Count; i++)
            {
                var a = _skillsBuffer[i];
                var b = _skillsSnapshot[i];
                if (a.name != b.name || a.prompt != b.prompt)
                    return true;
            }

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
