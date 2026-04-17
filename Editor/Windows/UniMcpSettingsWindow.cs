using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSettingsWindow : EditorWindow
    {
        private Vector2 _scroll;
        private string _buffer;

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
            _buffer = UniMcpSettings.instance.PrefabConvention;
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawPrefabConvention();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("UniMCP Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ProjectSettings/UniMcpSettings.asset (git 커밋 → 팀 공유).",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(6);
        }

        private void DrawPrefabConvention()
        {
            EditorGUILayout.LabelField("Prefab Convention", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "프리팹 네이밍·인스펙터 바인딩 규칙을 마크다운으로 작성합니다. 프리팹 검사 기능이 이 규칙을 참조합니다.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            if (!UniMcpSettings.instance.IsPrefabConventionDefined)
            {
                EditorGUILayout.HelpBox(
                    "프리팹 컨벤션이 비어 있습니다. 프리팹 검사 기능은 컨벤션이 정의된 뒤에만 동작합니다.",
                    MessageType.Warning);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            _buffer = EditorGUILayout.TextArea(
                _buffer ?? "",
                GUILayout.ExpandHeight(true),
                GUILayout.MinHeight(260));
            EditorGUILayout.EndScrollView();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload", GUILayout.Width(80)))
                    _buffer = UniMcpSettings.instance.PrefabConvention;

                GUILayout.FlexibleSpace();

                var dirty = _buffer != UniMcpSettings.instance.PrefabConvention;
                GUI.enabled = dirty;
                if (GUILayout.Button(dirty ? "Save *" : "Save", GUILayout.Width(100)))
                    UniMcpSettings.instance.PrefabConvention = _buffer;
                GUI.enabled = true;
            }
        }
    }
}
