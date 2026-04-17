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
        private const string SkillMdPath = "SKILL.md";

        private static readonly Color ColorAccent         = new(0.40f, 0.80f, 1.00f);
        private static readonly Color ColorDirty          = new(1.00f, 0.80f, 0.20f);
        private static readonly Color ColorDanger         = new(0.95f, 0.35f, 0.35f);
        private static readonly Color ColorMuted          = new(0.60f, 0.60f, 0.60f);
        private static readonly Color ColorBgCard         = new(0.18f, 0.18f, 0.20f);
        private static readonly Color ColorBgCardSelected = new(0.14f, 0.20f, 0.30f);
        private static readonly Color ColorSeparator      = new(0f, 0f, 0f, 0.3f);

        private const float SkillListWidth = 200f;
        private const float TreeWidth      = 240f;
        private const float CardHeight     = 46f;
        private const float TreeRowHeight  = 20f;

        [SerializeField] private List<UniMcpSkill> _skillsBuffer;
        [SerializeField] private List<UniMcpSkill> _skillsSnapshot;
        [SerializeField] private int _selectedSkillIdx = -1;
        [SerializeField] private string _selectedFilePath = SkillMdPath;
        [SerializeField] private List<string> _expandedFolders = new();

        [SerializeField] private Vector2 _skillsScroll;
        [SerializeField] private Vector2 _treeScroll;
        [SerializeField] private Vector2 _editorScroll;
        [SerializeField] private Vector2 _previewScroll;
        [SerializeField] private string _filter = "";
        [SerializeField] private bool _showPreview;

        private string _renameTarget;
        private string _renameBuffer;
        private bool _renameFocusPending;
        private bool _renameFocusArmed;
        private bool _pendingRenameCommit;

        private class TreeNode
        {
            public string name;
            public string fullPath;
            public bool isFolder;
            public List<TreeNode> children = new();
        }

        [MenuItem("UniMCP/Skill Generator")]
        private static void Open()
        {
            var existing = DockUtil.FindFirstOpen<UniMcpSkillGeneratorWindow>();
            if (existing != null)
            {
                existing.Focus();
                return;
            }

            EditorWindow anchor = DockUtil.FindFirstOpen<UniMcpWindow>();
            if (anchor == null) anchor = DockUtil.FindFirstOpen<UniMcpSettingsWindow>();
            if (anchor == null) anchor = DockUtil.FindFirstOpen<UniMcpSkillExecutorWindow>();

            if (anchor == null)
            {
                var w = GetWindow<UniMcpSkillGeneratorWindow>("Skill Generator");
                w.minSize = new Vector2(820, 560);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSkillGeneratorWindow>();
            window.titleContent = new GUIContent("Skill Generator");
            window.minSize = new Vector2(820, 560);

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

            _selectedFilePath = SkillMdPath;
            _renameTarget = null;
            _renameBuffer = null;
        }

        private void OnGUI()
        {
            HandleGlobalShortcuts();

            if (_pendingRenameCommit && Event.current.type == EventType.Layout)
            {
                _pendingRenameCommit = false;
                CommitRename();
            }

            DrawToolbar();

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                DrawSkillList();
                DrawVerticalSeparator();
                DrawFileTree();
                DrawVerticalSeparator();
                DrawEditor();
            }

            DrawStatusBar();
        }

        private void HandleGlobalShortcuts()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown)
                return;

            if (e.keyCode == KeyCode.F2
                && _renameTarget == null
                && _selectedSkillIdx >= 0
                && !string.IsNullOrEmpty(_selectedFilePath)
                && _selectedFilePath != SkillMdPath)
            {
                BeginRename(_selectedFilePath);
                e.Use();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("🔍", GUILayout.Width(18));
                _filter = EditorGUILayout.TextField(
                    _filter,
                    EditorStyles.toolbarSearchField,
                    GUILayout.MinWidth(140));

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

        private void DrawSkillList()
        {
            using (new EditorGUILayout.VerticalScope(
                       GUILayout.Width(SkillListWidth),
                       GUILayout.ExpandHeight(true)))
            {
                var filtered = GetFilteredIndices();

                if (filtered.Count == 0)
                {
                    EditorGUILayout.Space(12);
                    var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                    var msg = _skillsBuffer.Count == 0
                        ? "스킬 없음"
                        : $"'{_filter}'와\n일치하는 스킬 없음";
                    EditorGUILayout.LabelField(msg, style);
                    GUILayout.FlexibleSpace();
                    return;
                }

                _skillsScroll = EditorGUILayout.BeginScrollView(_skillsScroll, GUILayout.ExpandHeight(true));
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
                if (_selectedSkillIdx != i)
                {
                    _selectedSkillIdx = i;
                    _selectedFilePath = SkillMdPath;
                    GUI.FocusControl(null);
                }
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
                GetSkillSubtitle(skill),
                previewStyle);

            return removed;
        }

        private static string GetSkillSubtitle(UniMcpSkill skill)
        {
            var fileCount = skill.files?.Count ?? 0;
            var folderCount = skill.folders?.Count ?? 0;
            if (fileCount == 0 && folderCount == 0)
                return "SKILL.md";
            return $"SKILL.md + {fileCount} file{(fileCount == 1 ? "" : "s")}"
                   + (folderCount > 0 ? $", {folderCount} folder{(folderCount == 1 ? "" : "s")}" : "");
        }

        private void DrawVerticalSeparator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.Width(1), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(rect, ColorSeparator);
        }

        private void DrawFileTree()
        {
            using (new EditorGUILayout.VerticalScope(
                       GUILayout.Width(TreeWidth),
                       GUILayout.ExpandHeight(true)))
            {
                if (_skillsBuffer.Count == 0 || _selectedSkillIdx < 0)
                {
                    EditorGUILayout.Space(12);
                    EditorGUILayout.LabelField(
                        "스킬 선택",
                        new GUIStyle(EditorStyles.centeredGreyMiniLabel));
                    GUILayout.FlexibleSpace();
                    return;
                }

                var skill = _skillsBuffer[_selectedSkillIdx];

                DrawTreeHint();

                _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll, GUILayout.ExpandHeight(true));

                DrawTreeRow(SkillMdPath, "SKILL.md", depth: 0, isFolder: false, isPinned: true);

                var tree = BuildTree(skill);
                foreach (var node in tree)
                    DrawTreeNodeRecursive(node, depth: 0);

                DrawTreeRootDropZone(skill);

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawTreeHint()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var style = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = ColorMuted },
                    alignment = TextAnchor.MiddleLeft,
                };
                GUILayout.Label("우클릭 메뉴 · F2 rename · 드래그로 이동", style);
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawTreeRootDropZone(UniMcpSkill skill)
        {
            var fillRect = GUILayoutUtility.GetRect(
                0, 60,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            HandleRowDragDrop(skill, fillRect, "");
            HandleRowContextMenu(skill, fillRect, path: "", isFolder: true, isPinned: false);
        }

        private void DrawTreeNodeRecursive(TreeNode node, int depth)
        {
            var expanded = IsExpanded(node.fullPath);
            DrawTreeRow(node.fullPath, node.name, depth, node.isFolder, isPinned: false, expanded);

            if (node.isFolder && expanded)
            {
                foreach (var child in node.children)
                    DrawTreeNodeRecursive(child, depth + 1);
            }
        }

        private void DrawTreeRow(string path, string displayName, int depth, bool isFolder, bool isPinned, bool expanded = false)
        {
            var rect = GUILayoutUtility.GetRect(0, TreeRowHeight, GUILayout.ExpandWidth(true));
            var selected = _selectedFilePath == path;
            var dirty = IsFileDirty(path);
            var skill = _skillsBuffer[_selectedSkillIdx];

            if (selected)
                EditorGUI.DrawRect(rect, ColorBgCardSelected);

            var indent = 12 + depth * 14;
            var cursor = rect.x + indent;

            if (isFolder)
            {
                var arrowRect = new Rect(cursor, rect.y + 2, 14, 16);
                var arrow = expanded ? "▾" : "▸";
                if (GUI.Button(arrowRect, arrow, EditorStyles.label))
                    ToggleExpanded(path);
                cursor += 14;
            }
            else
            {
                cursor += 14;
            }

            var iconRect = new Rect(cursor, rect.y + 2, 16, 16);
            GUI.Label(iconRect, isFolder ? "📁" : "📄");
            cursor += 16;

            var labelRect = new Rect(cursor, rect.y + 2, rect.width - (cursor - rect.x) - 18, 16);

            if (_renameTarget == path)
            {
                GUI.SetNextControlName("RenameField");
                var newName = GUI.TextField(labelRect, _renameBuffer ?? "");
                _renameBuffer = newName;

                if (_renameFocusPending && Event.current.type == EventType.Repaint)
                {
                    EditorGUI.FocusTextInControl("RenameField");
                    _renameFocusPending = false;
                }

                var evt = Event.current;
                if (evt.type == EventType.KeyDown)
                {
                    if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        CommitRename();
                        evt.Use();
                    }
                    else if (evt.keyCode == KeyCode.Escape)
                    {
                        CancelRename();
                        evt.Use();
                    }
                }

                if (Event.current.type == EventType.Repaint && !_renameFocusPending)
                {
                    var focused = GUI.GetNameOfFocusedControl();
                    if (!_renameFocusArmed)
                    {
                        if (focused == "RenameField")
                            _renameFocusArmed = true;
                    }
                    else if (focused != "RenameField")
                    {
                        _pendingRenameCommit = true;
                        Repaint();
                    }
                }
            }
            else
            {
                var labelStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                };
                if (isPinned)
                    labelStyle.normal.textColor = ColorAccent;

                GUI.Label(labelRect, displayName, labelStyle);

                if (dirty)
                {
                    var dotRect = new Rect(rect.x + rect.width - 14, rect.y + 6, 8, 8);
                    EditorGUI.DrawRect(dotRect, ColorDirty);
                }
            }

            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && rect.Contains(Event.current.mousePosition)
                && _renameTarget != path)
            {
                if (_renameTarget != null)
                    CommitRename();

                _selectedFilePath = path;
                if (isFolder && Event.current.clickCount >= 2)
                    ToggleExpanded(path);
                GUI.FocusControl(null);
                Repaint();
            }

            HandleRowDragInitiate(rect, path, displayName, isFolder, isPinned);
            HandleRowDragDrop(skill, rect, isFolder ? path : GetParentFolder(path));
            HandleRowContextMenu(skill, rect, path, isFolder, isPinned);
        }

        private void HandleRowDragInitiate(Rect rect, string path, string displayName, bool isFolder, bool isPinned)
        {
            if (isPinned || string.IsNullOrEmpty(path))
                return;
            if (_renameTarget == path)
                return;

            var evt = Event.current;
            if (evt.type != EventType.MouseDrag)
                return;
            if (!rect.Contains(evt.mousePosition))
                return;

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData("UniMcpTreeNodePath", path);
            DragAndDrop.SetGenericData("UniMcpTreeNodeIsFolder", isFolder);
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.StartDrag($"Move {displayName}");
            evt.Use();
        }

        private static string GetParentFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path.Substring(0, slash);
        }

        private void HandleRowDragDrop(UniMcpSkill skill, Rect rect, string targetFolder)
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
                return;
            if (!rect.Contains(evt.mousePosition))
                return;

            var sourcePath = DragAndDrop.GetGenericData("UniMcpTreeNodePath") as string;
            if (string.IsNullOrEmpty(sourcePath))
                return;

            var sourceIsFolder = false;
            var isFolderObj = DragAndDrop.GetGenericData("UniMcpTreeNodeIsFolder");
            if (isFolderObj is bool b) sourceIsFolder = b;

            if (!CanMoveTo(sourcePath, sourceIsFolder, targetFolder))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                evt.Use();
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Move;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                MoveNode(skill, sourcePath, sourceIsFolder, targetFolder);
                evt.Use();
                Repaint();
            }
            else
            {
                evt.Use();
            }
        }

        private static bool CanMoveTo(string sourcePath, bool sourceIsFolder, string targetFolder)
        {
            if (string.IsNullOrEmpty(sourcePath) || sourcePath == SkillMdPath)
                return false;

            targetFolder = targetFolder ?? "";
            var currentParent = GetParentFolder(sourcePath);
            if (currentParent == targetFolder)
                return false;

            if (sourceIsFolder)
            {
                if (string.Equals(targetFolder, sourcePath, StringComparison.Ordinal))
                    return false;
                if (targetFolder.StartsWith(sourcePath + "/", StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private void MoveNode(UniMcpSkill skill, string sourcePath, bool sourceIsFolder, string targetFolder)
        {
            targetFolder = targetFolder ?? "";
            var slash = sourcePath.LastIndexOf('/');
            var leaf = slash < 0 ? sourcePath : sourcePath.Substring(slash + 1);
            var newPath = string.IsNullOrEmpty(targetFolder) ? leaf : targetFolder + "/" + leaf;

            if (IsPathTaken(skill, newPath))
            {
                var baseName = leaf;
                var ext = "";
                if (!sourceIsFolder)
                {
                    var dot = leaf.LastIndexOf('.');
                    if (dot > 0)
                    {
                        baseName = leaf.Substring(0, dot);
                        ext = leaf.Substring(dot);
                    }
                }
                int n = 2;
                while (true)
                {
                    var candidate = $"{baseName}-{n}{ext}";
                    var candidatePath = string.IsNullOrEmpty(targetFolder) ? candidate : targetFolder + "/" + candidate;
                    if (!IsPathTaken(skill, candidatePath))
                    {
                        newPath = candidatePath;
                        break;
                    }
                    n++;
                }
            }

            if (sourceIsFolder)
            {
                var idx = skill.folders.FindIndex(f => f == sourcePath);
                if (idx >= 0)
                    skill.folders[idx] = newPath;
                RetargetPathsUnder(skill, sourcePath, newPath);
            }
            else
            {
                var idx = skill.files.FindIndex(f => f.path == sourcePath);
                if (idx >= 0)
                    skill.files[idx].path = newPath;
            }

            for (int i = 0; i < _expandedFolders.Count; i++)
            {
                var ef = _expandedFolders[i];
                if (ef == sourcePath)
                    _expandedFolders[i] = newPath;
                else if (ef.StartsWith(sourcePath + "/", StringComparison.Ordinal))
                    _expandedFolders[i] = newPath + ef.Substring(sourcePath.Length);
            }

            if (_selectedFilePath == sourcePath)
                _selectedFilePath = newPath;

            if (!string.IsNullOrEmpty(targetFolder) && !_expandedFolders.Contains(targetFolder))
                _expandedFolders.Add(targetFolder);
        }

        private void HandleRowContextMenu(UniMcpSkill skill, Rect rect, string path, bool isFolder, bool isPinned)
        {
            var evt = Event.current;
            if (evt.type != EventType.ContextClick)
                return;
            if (!rect.Contains(evt.mousePosition))
                return;

            if (!string.IsNullOrEmpty(path))
                _selectedFilePath = path;

            ShowContextMenu(skill, path, isFolder, isPinned);
            evt.Use();
        }

        private void ShowContextMenu(UniMcpSkill skill, string path, bool isFolder, bool isPinned)
        {
            var menu = new GenericMenu();

            string parent;
            if (string.IsNullOrEmpty(path) || path == SkillMdPath)
                parent = "";
            else if (isFolder)
                parent = path;
            else
                parent = GetParentFolder(path);

            menu.AddItem(new GUIContent("+ New File"), false, () => BeginAddItem(skill, false, parent));
            menu.AddItem(new GUIContent("+ New Folder"), false, () => BeginAddItem(skill, true, parent));

            if (!isPinned && !string.IsNullOrEmpty(path))
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Rename  F2"), false, () => BeginRename(path));
                menu.AddItem(new GUIContent("Delete"), false, () =>
                {
                    _selectedFilePath = path;
                    DeleteSelected(skill);
                });
            }

            menu.ShowAsContext();
        }


        private bool IsExpanded(string folderPath)
        {
            return _expandedFolders.Contains(folderPath);
        }

        private void ToggleExpanded(string folderPath)
        {
            if (_expandedFolders.Contains(folderPath))
                _expandedFolders.Remove(folderPath);
            else
                _expandedFolders.Add(folderPath);
            Repaint();
        }

        private static List<TreeNode> BuildTree(UniMcpSkill skill)
        {
            var root = new TreeNode { name = "", fullPath = "", isFolder = true };

            foreach (var folder in skill.folders ?? new List<string>())
                EnsureFolderPath(root, folder);

            foreach (var file in skill.files ?? new List<UniMcpSkillFile>())
            {
                if (string.IsNullOrEmpty(file?.path))
                    continue;
                var segments = file.path.Replace('\\', '/').Split('/');
                var current = root;
                for (int i = 0; i < segments.Length - 1; i++)
                    current = EnsureChildFolder(current, segments[i]);
                current.children.Add(new TreeNode
                {
                    name = segments[segments.Length - 1],
                    fullPath = file.path.Replace('\\', '/'),
                    isFolder = false,
                });
            }

            SortRecursive(root);
            return root.children;
        }

        private static TreeNode EnsureFolderPath(TreeNode root, string folderPath)
        {
            var segments = (folderPath ?? "").Replace('\\', '/').Split('/');
            var current = root;
            foreach (var seg in segments)
            {
                if (string.IsNullOrEmpty(seg)) continue;
                current = EnsureChildFolder(current, seg);
            }
            return current;
        }

        private static TreeNode EnsureChildFolder(TreeNode parent, string name)
        {
            foreach (var c in parent.children)
                if (c.isFolder && c.name == name)
                    return c;

            var parentPath = parent.fullPath ?? "";
            var fullPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
            var node = new TreeNode { name = name, fullPath = fullPath, isFolder = true };
            parent.children.Add(node);
            return node;
        }

        private static void SortRecursive(TreeNode node)
        {
            if (node.children == null) return;
            node.children.Sort((a, b) =>
            {
                if (a.isFolder != b.isFolder) return a.isFolder ? -1 : 1;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var c in node.children)
                SortRecursive(c);
        }

        private void BeginAddItem(UniMcpSkill skill, bool isFolder, string parent = null)
        {
            parent = parent ?? GetSelectedFolderPath();
            var defaultName = isFolder ? "new-folder" : "new-file.md";

            var newPath = string.IsNullOrEmpty(parent) ? defaultName : parent + "/" + defaultName;

            int n = 2;
            while (IsPathTaken(skill, newPath))
            {
                var withSuffix = isFolder
                    ? $"new-folder-{n}"
                    : $"new-file-{n}.md";
                newPath = string.IsNullOrEmpty(parent) ? withSuffix : parent + "/" + withSuffix;
                n++;
            }

            if (isFolder)
                skill.folders.Add(newPath);
            else
                skill.files.Add(new UniMcpSkillFile { path = newPath, content = "" });

            if (!string.IsNullOrEmpty(parent) && !_expandedFolders.Contains(parent))
                _expandedFolders.Add(parent);

            _selectedFilePath = newPath;
            BeginRename(newPath);
        }

        private string GetSelectedFolderPath()
        {
            if (string.IsNullOrEmpty(_selectedFilePath) || _selectedFilePath == SkillMdPath)
                return "";

            var skill = _skillsBuffer[_selectedSkillIdx];
            if (skill.folders != null && skill.folders.Any(f => f == _selectedFilePath))
                return _selectedFilePath;

            var slash = _selectedFilePath.LastIndexOf('/');
            return slash < 0 ? "" : _selectedFilePath.Substring(0, slash);
        }

        private bool IsPathTaken(UniMcpSkill skill, string path)
        {
            if (skill.folders != null && skill.folders.Any(f => f == path))
                return true;
            if (skill.files != null && skill.files.Any(f => f.path == path))
                return true;
            return false;
        }

        private void BeginRename(string path)
        {
            if (string.IsNullOrEmpty(path) || path == SkillMdPath)
                return;
            _renameTarget = path;
            var slash = path.LastIndexOf('/');
            _renameBuffer = slash < 0 ? path : path.Substring(slash + 1);
            _renameFocusPending = true;
            _renameFocusArmed = false;
            Repaint();
        }

        private void CommitRename()
        {
            if (_renameTarget == null)
                return;

            var skill = _skillsBuffer[_selectedSkillIdx];
            var oldPath = _renameTarget;
            var slash = oldPath.LastIndexOf('/');
            var parent = slash < 0 ? "" : oldPath.Substring(0, slash);
            var newName = (_renameBuffer ?? "").Trim();

            if (string.IsNullOrEmpty(newName) || newName.Contains("/") || newName.Contains("\\"))
            {
                CancelRename();
                return;
            }

            var newPath = string.IsNullOrEmpty(parent) ? newName : parent + "/" + newName;
            if (newPath == oldPath)
            {
                CancelRename();
                return;
            }

            if (IsPathTaken(skill, newPath))
            {
                EditorUtility.DisplayDialog("Rename", "같은 경로에 이미 존재합니다.", "OK");
                CancelRename();
                return;
            }

            if (skill.folders != null)
            {
                var folderIdx = skill.folders.FindIndex(f => f == oldPath);
                if (folderIdx >= 0)
                {
                    skill.folders[folderIdx] = newPath;
                    RetargetPathsUnder(skill, oldPath, newPath);
                }
            }

            if (skill.files != null)
            {
                var fileIdx = skill.files.FindIndex(f => f.path == oldPath);
                if (fileIdx >= 0)
                    skill.files[fileIdx].path = newPath;
            }

            for (int i = 0; i < _expandedFolders.Count; i++)
            {
                var ef = _expandedFolders[i];
                if (ef == oldPath) _expandedFolders[i] = newPath;
                else if (ef.StartsWith(oldPath + "/", StringComparison.Ordinal))
                    _expandedFolders[i] = newPath + ef.Substring(oldPath.Length);
            }

            _selectedFilePath = newPath;
            _renameTarget = null;
            _renameBuffer = null;
            _renameFocusPending = false;
            _renameFocusArmed = false;
            Repaint();
        }

        private void CancelRename()
        {
            _renameTarget = null;
            _renameBuffer = null;
            _renameFocusPending = false;
            _renameFocusArmed = false;
            Repaint();
        }

        private static void RetargetPathsUnder(UniMcpSkill skill, string oldPrefix, string newPrefix)
        {
            var oldPrefixSlash = oldPrefix + "/";
            var newPrefixSlash = newPrefix + "/";

            if (skill.folders != null)
            {
                for (int i = 0; i < skill.folders.Count; i++)
                {
                    if (skill.folders[i].StartsWith(oldPrefixSlash, StringComparison.Ordinal))
                        skill.folders[i] = newPrefixSlash + skill.folders[i].Substring(oldPrefixSlash.Length);
                }
            }

            if (skill.files != null)
            {
                foreach (var f in skill.files)
                {
                    if (f.path.StartsWith(oldPrefixSlash, StringComparison.Ordinal))
                        f.path = newPrefixSlash + f.path.Substring(oldPrefixSlash.Length);
                }
            }
        }

        private void DeleteSelected(UniMcpSkill skill)
        {
            var path = _selectedFilePath;
            if (string.IsNullOrEmpty(path) || path == SkillMdPath)
                return;

            var isFolder = skill.folders != null && skill.folders.Contains(path);
            var confirm = EditorUtility.DisplayDialog(
                "Delete",
                isFolder
                    ? $"폴더 '{path}'와 하위 파일을 모두 삭제합니까?"
                    : $"파일 '{path}'을(를) 삭제합니까?",
                "Delete",
                "Cancel");
            if (!confirm)
                return;

            if (isFolder)
            {
                skill.folders.RemoveAll(f => f == path || f.StartsWith(path + "/", StringComparison.Ordinal));
                skill.files.RemoveAll(f => f.path.StartsWith(path + "/", StringComparison.Ordinal));
                _expandedFolders.RemoveAll(e => e == path || e.StartsWith(path + "/", StringComparison.Ordinal));
            }
            else
            {
                skill.files.RemoveAll(f => f.path == path);
            }

            _selectedFilePath = SkillMdPath;
            Repaint();
        }

        private void DrawEditor()
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

                DrawEditorForSelected();
            }
        }

        private void DrawEditorForSelected()
        {
            var skill = _skillsBuffer[_selectedSkillIdx];

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Skill", GUILayout.Width(50));
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

            var invocation = SkillStore.GetInvocationName(skill.name);
            if (!string.IsNullOrEmpty(invocation))
            {
                var hintStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = ColorMuted },
                };
                EditorGUILayout.LabelField($"Invoke in chat: /{invocation}", hintStyle);
            }

            if (_selectedFilePath == SkillMdPath)
                DrawSkillMdEditor(skill);
            else if (skill.folders != null && skill.folders.Contains(_selectedFilePath))
                DrawFolderInfo(_selectedFilePath);
            else
                DrawFileEditor(skill);
        }

        private void DrawSkillMdEditor(UniMcpSkill skill)
        {
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("SKILL.md (markdown)", EditorStyles.boldLabel);
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
                        GUILayout.MinHeight(240));

                    skill.prompt = EditorGUILayout.TextArea(
                        skill.prompt ?? "",
                        GUILayout.ExpandHeight(true));

                    EditorGUILayout.EndScrollView();
                }

                if (_showPreview)
                    DrawPreviewPane(skill.prompt);
            }

            var (chars, lines) = CountCharsLines(skill.prompt);
            EditorGUILayout.LabelField($"{chars:N0} chars · {lines} lines", EditorStyles.miniLabel);
        }

        private void DrawFileEditor(UniMcpSkill skill)
        {
            var file = skill.files?.FirstOrDefault(f => f.path == _selectedFilePath);
            if (file == null)
            {
                EditorGUILayout.HelpBox("선택한 파일을 찾을 수 없습니다.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(file.path, EditorStyles.boldLabel);
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
                        GUILayout.MinHeight(240));

                    file.content = EditorGUILayout.TextArea(
                        file.content ?? "",
                        GUILayout.ExpandHeight(true));

                    EditorGUILayout.EndScrollView();
                }

                if (_showPreview && file.path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    DrawPreviewPane(file.content);
            }

            var (chars, lines) = CountCharsLines(file.content);
            EditorGUILayout.LabelField($"{chars:N0} chars · {lines} lines", EditorStyles.miniLabel);
        }

        private void DrawPreviewPane(string content)
        {
            using (new EditorGUILayout.VerticalScope(
                       EditorStyles.helpBox,
                       GUILayout.ExpandHeight(true),
                       GUILayout.Width(position.width * 0.35f)))
            {
                _previewScroll = EditorGUILayout.BeginScrollView(
                    _previewScroll,
                    GUILayout.ExpandHeight(true));

                var rendered = MarkdownRenderer.ToRichText(content ?? "");
                var style = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
                EditorGUILayout.LabelField(rendered, style);

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawFolderInfo(string folderPath)
        {
            var skill = _skillsBuffer[_selectedSkillIdx];
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Folder", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(folderPath, EditorStyles.miniLabel);
            EditorGUILayout.Space(8);

            var childFiles = (skill.files ?? new List<UniMcpSkillFile>())
                .Where(f => f.path.StartsWith(folderPath + "/", StringComparison.Ordinal))
                .ToList();
            var childFolders = (skill.folders ?? new List<string>())
                .Where(f => f != folderPath
                         && f.StartsWith(folderPath + "/", StringComparison.Ordinal))
                .ToList();

            EditorGUILayout.LabelField($"하위 파일: {childFiles.Count}, 하위 폴더: {childFolders.Count}", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "상단 툴바의 + File / + Folder 로 이 폴더에 항목을 추가할 수 있습니다. Rename / Delete 는 하단 바에서.",
                MessageType.Info);
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
                    result.Add(i);
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
            _selectedFilePath = SkillMdPath;
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
            _selectedFilePath = SkillMdPath;
            Repaint();
        }

        private bool IsSkillDirty(int i)
        {
            if (_skillsSnapshot == null || i >= _skillsSnapshot.Count)
                return true;

            var a = _skillsBuffer[i];
            var b = _skillsSnapshot[i];
            if (a.name != b.name || a.prompt != b.prompt)
                return true;

            var aFolders = a.folders ?? new List<string>();
            var bFolders = b.folders ?? new List<string>();
            if (!aFolders.OrderBy(x => x).SequenceEqual(bFolders.OrderBy(x => x)))
                return true;

            var aFiles = a.files ?? new List<UniMcpSkillFile>();
            var bFiles = b.files ?? new List<UniMcpSkillFile>();
            if (aFiles.Count != bFiles.Count)
                return true;

            foreach (var af in aFiles)
            {
                var bf = bFiles.FirstOrDefault(x => x.path == af.path);
                if (bf == null) return true;
                if (bf.content != af.content) return true;
            }
            return false;
        }

        private bool IsFileDirty(string path)
        {
            if (_selectedSkillIdx < 0 || _selectedSkillIdx >= _skillsBuffer.Count)
                return false;
            if (_skillsSnapshot == null || _selectedSkillIdx >= _skillsSnapshot.Count)
                return true;

            var cur = _skillsBuffer[_selectedSkillIdx];
            var snap = _skillsSnapshot[_selectedSkillIdx];

            if (path == SkillMdPath)
                return cur.prompt != snap.prompt;

            if (cur.folders != null && cur.folders.Contains(path))
                return snap.folders == null || !snap.folders.Contains(path);

            var curFile = cur.files?.FirstOrDefault(f => f.path == path);
            var snapFile = snap.files?.FirstOrDefault(f => f.path == path);
            if (curFile == null) return false;
            if (snapFile == null) return true;
            return curFile.content != snapFile.content;
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
                EditorUtility.DisplayDialog("Invalid Skills", "이름이 비어 있는 스킬이 있습니다.", "OK");
                return;
            }
            if (names.Distinct().Count() != names.Count)
            {
                EditorUtility.DisplayDialog("Duplicate Names", "스킬 이름이 중복됩니다.", "OK");
                return;
            }

            SkillStore.Sync(_skillsSnapshot, _skillsBuffer);
            UniMcpSettings.instance.SetSkills(_skillsBuffer);
            _skillsSnapshot = _skillsBuffer.Select(s => s.Clone()).ToList();
        }
    }
}
