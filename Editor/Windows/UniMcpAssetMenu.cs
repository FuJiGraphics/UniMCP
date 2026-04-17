using System.Linq;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// Project 창 우클릭 메뉴에 UniMCP 스킬 실행 엔트리를 추가한다.
    /// 하나의 MenuItem에서 GenericMenu 팝업을 띄워 사용자가 스킬을 고르고, 선택 대상에 대해 Skill Executor로 자동 실행한다
    /// </summary>
    public static class UniMcpAssetMenu
    {
        private const string MenuPath = "Assets/UniMCP/Run Skill\u2026";

        [MenuItem(MenuPath, false, 500)]
        private static void RunSkillOnSelection()
        {
            var selection = Selection.objects;
            if (selection == null || selection.Length == 0)
                return;

            var skills = UniMcpSettings.instance.Skills;
            if (skills.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "No UniMCP Skills",
                    "스킬이 아직 없습니다. UniMCP → Skill Generator 에서 먼저 만들어주세요.",
                    "OK");
                return;
            }

            var menu = new GenericMenu();
            foreach (var skill in skills)
            {
                var captured = skill;
                menu.AddItem(new GUIContent(skill.name), false, () =>
                {
                    var window = UniMcpSkillExecutorWindow.GetOrCreateWindow();
                    window.PresetAndRun(captured, Selection.objects);
                });
            }
            menu.ShowAsContext();
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateRunSkillOnSelection()
        {
            return Selection.objects != null
                && Selection.objects.Any(o => o != null
                    && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));
        }
    }
}
