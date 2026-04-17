using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpWindow : EditorWindow
    {
        [MenuItem("UniMCP/Open Window")]
        private static void Open()
        {
            var window = GetWindow<UniMcpWindow>("UniMCP");
            window.minSize = new Vector2(420, 320);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("UniMCP", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Unity Editor MCP SDK", EditorStyles.miniLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Scaffold window. Bridge server, tool registry, and skill workflows arrive in Phase 1.",
                MessageType.Info);
        }
    }
}
