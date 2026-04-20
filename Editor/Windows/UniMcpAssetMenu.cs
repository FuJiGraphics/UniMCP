using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniMCP.Editor.Logging;
using UniMCP.Editor.PrefabHook;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// Project 창 우클릭 메뉴에 UI 컨벤션 수정 스킬을 단일 엔트리로 등록한다.
    /// Editor 언어가 한국어면 "UI 컨벤션 수정", 그 외엔 "Fix UI Convention" 으로 표기
    /// </summary>
    [InitializeOnLoad]
    public static class UniMcpAssetMenu
    {
        private const string MenuPathKo = "Assets/UniMCP/Run/UI 컨벤션 수정";
        private const string MenuPathEn = "Assets/UniMCP/Run/Fix UI Convention";

        private static string _registeredPath;

        static UniMcpAssetMenu()
        {
            EditorApplication.delayCall += Register;
        }

        private static void Register()
        {
            var path = IsKoreanEditor() ? MenuPathKo : MenuPathEn;
            if (TryAddMenuItem(path, priority: 500, RunConventionSkill, HasAssetSelection))
                _registeredPath = path;
        }

        private static void RunConventionSkill()
        {
            var skill = BuiltinSkills.GetUiConventionSkill();
            var paths = new List<string>();
            foreach (var o in Selection.objects ?? Array.Empty<UnityEngine.Object>())
            {
                var resolved = UniMcpSkillExecutorWindow.ResolveToAsset(o);
                if (resolved == null) continue;

                var p = AssetDatabase.GetAssetPath(resolved);
                if (!string.IsNullOrEmpty(p) && !paths.Contains(p))
                    paths.Add(p);
            }

            if (paths.Count == 0)
                return;

            UniMcpRunQueue.Enqueue(skill, paths, isBuiltin: true);
        }

        /// <summary>
        /// Unity Editor 언어가 한국어인지 확인.
        /// EditorPrefs 우선, 실패 시 시스템 언어 폴백
        /// </summary>
        private static bool IsKoreanEditor()
        {
            try
            {
                var locale = EditorPrefs.GetString("Editor.kEditorLocale", "");
                if (!string.IsNullOrEmpty(locale))
                    return locale.Equals("Korean", StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            return Application.systemLanguage == SystemLanguage.Korean;
        }

        private static bool HasAssetSelection()
        {
            return Selection.objects != null
                && Selection.objects.Any(o => o != null
                    && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));
        }

        private static bool TryAddMenuItem(string name, int priority, Action execute, Func<bool> validate)
        {
            try
            {
                var method = typeof(UnityEditor.Menu).GetMethod(
                    "AddMenuItem",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                    return false;

                method.Invoke(null, new object[] { name, "", false, priority, execute, validate });
                return true;
            }
            catch (Exception e)
            {
                UniMcpLogger.Warn("AddMenuItem reflection failed: " + e.Message);
                return false;
            }
        }
    }
}
