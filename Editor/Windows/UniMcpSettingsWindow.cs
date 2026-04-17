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
            var window = GetWindow<UniMcpSettingsWindow>("UniMCP Settings");
            window.minSize = new Vector2(520, 460);
            window.Show();
        }

        private void OnEnable()
        {
            _buffer = UniMcpSettings.instance.PrefabConvention;
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawPrefabConventionSection();
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

        private void DrawPrefabConventionSection()
        {
            EditorGUILayout.LabelField("Prefab Convention", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Define naming / inspector-binding rules here in markdown. Claude uses this when inspecting prefabs.",
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
