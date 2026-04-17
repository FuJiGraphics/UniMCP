using System.Collections.Generic;
using System.Linq;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSettingsWindow : EditorWindow
    {
        private enum eTab
        {
            PrefabConvention = 0,
            Skills = 1,
        }

        private eTab _tab = eTab.PrefabConvention;
        private Vector2 _conventionScroll;
        private Vector2 _skillPromptScroll;

        private string _conventionBuffer;
        private List<UniMcpSkill> _skillsBuffer;
        private List<UniMcpSkill> _skillsSnapshot;
        private int _selectedSkillIdx;
        private string _newSkillName = "";

        [MenuItem("UniMCP/Settings")]
        private static void Open()
        {
            var existing = DockUtil.FindFirstOpen<UniMcpSettingsWindow>();
            if (existing != null)
            {
                existing.Focus();
                return;
            }

            var chat = DockUtil.FindFirstOpen<UniMcpWindow>();
            if (chat == null)
            {
                var w = GetWindow<UniMcpSettingsWindow>("UniMCP Settings");
                w.minSize = new Vector2(520, 460);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSettingsWindow>();
            window.titleContent = new GUIContent("UniMCP Settings");
            window.minSize = new Vector2(520, 460);

            if (!DockUtil.TryDockNextTo(chat, window))
                window.Show();
        }

        private void OnEnable()
        {
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            var s = UniMcpSettings.instance;
            _conventionBuffer = s.PrefabConvention;
            _skillsBuffer = s.Skills.Select(x => x.Clone()).ToList();
            _skillsSnapshot = s.Skills.Select(x => x.Clone()).ToList();
            _selectedSkillIdx = 0;
            _newSkillName = "";
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawTabs();
            EditorGUILayout.Space(4);

            switch (_tab)
            {
                case eTab.PrefabConvention: DrawPrefabConvention(); break;
                case eTab.Skills:            DrawSkills(); break;
            }

            EditorGUILayout.Space(4);
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("UniMCP Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Shared via ProjectSettings/UniMcpSettings.asset (commit to git for team sync).",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(6);
        }

        private void DrawTabs()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                DrawTabButton("Prefab Convention", eTab.PrefabConvention);
                DrawTabButton("Skills", eTab.Skills);
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawTabButton(string label, eTab tab)
        {
            var selected = _tab == tab;
            var style = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
            };
            if (GUILayout.Toggle(selected, label, style) && !selected)
                _tab = tab;
        }

        private void DrawPrefabConvention()
        {
            EditorGUILayout.LabelField(
                "Describe prefab naming and binding rules in markdown. Claude reads this when inspecting prefabs.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            if (string.IsNullOrWhiteSpace(_conventionBuffer))
            {
                EditorGUILayout.HelpBox(
                    "프리팹 컨벤션이 비어 있습니다. 프리팹 검사 기능은 컨벤션이 정의된 뒤에만 동작합니다.",
                    MessageType.Warning);
            }

            _conventionScroll = EditorGUILayout.BeginScrollView(
                _conventionScroll,
                GUILayout.ExpandHeight(true));
            _conventionBuffer = EditorGUILayout.TextArea(
                _conventionBuffer ?? "",
                GUILayout.ExpandHeight(true),
                GUILayout.MinHeight(280));
            EditorGUILayout.EndScrollView();
        }

        private void DrawSkills()
        {
            EditorGUILayout.LabelField(
                "Project-specific skills are saved to .claude/skills/<name>/SKILL.md so Claude picks them up automatically. Invoke in chat via /<name>.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            DrawSkillList();
            EditorGUILayout.Space(4);
            DrawSkillAddRow();
            EditorGUILayout.Space(8);
            DrawSkillEditor();
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
                GUILayout.MinHeight(200));
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
            if (_conventionBuffer != UniMcpSettings.instance.PrefabConvention)
                return true;

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

            UniMcpSettings.instance.PrefabConvention = _conventionBuffer ?? "";
            UniMcpSettings.instance.SetSkills(_skillsBuffer);

            _skillsSnapshot = _skillsBuffer.Select(s => s.Clone()).ToList();
        }
    }
}
