using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// Project 창 우클릭 메뉴에 UniMCP 스킬을 개별 서브메뉴로 동적 등록한다.
    /// 선택된 스킬은 창을 열지 않고 백그라운드에서 바로 실행되며 진행 상황은 Unity Progress 윈도우에 표시된다.
    /// 내부 `Menu.AddMenuItem` API 변경 시 단일 picker 엔트리로 폴백한다
    /// </summary>
    [InitializeOnLoad]
    public static class UniMcpAssetMenu
    {
        private const string MenuRoot = "Assets/UniMCP/Run/";
        private const string FallbackPath = "Assets/UniMCP/Run Skill\u2026";

        private static readonly List<string> Registered = new();
        private static bool _fallbackRegistered;

        static UniMcpAssetMenu()
        {
            EditorApplication.delayCall += () =>
            {
                Rebuild();
                UniMcpSettings.SkillsChanged -= Rebuild;
                UniMcpSettings.SkillsChanged += Rebuild;
            };
        }

        private static void Rebuild()
        {
            UnregisterAll();

            var skills = UniMcpSettings.instance.Skills;
            if (skills.Count == 0)
                return;

            int priority = 500;
            bool dynamicOk = true;
            foreach (var skill in skills)
            {
                var captured = skill;
                var path = MenuRoot + EscapeMenuPath(skill.name);
                if (!TryAddMenuItem(path, priority++, () => RunSkillAsync(captured), HasAssetSelection))
                {
                    dynamicOk = false;
                    break;
                }
                Registered.Add(path);
            }

            if (!dynamicOk)
            {
                UnregisterAll();
                EnsureFallbackRegistered();
            }
        }

        private static void UnregisterAll()
        {
            foreach (var p in Registered)
                TryRemoveMenuItem(p);
            Registered.Clear();
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
                Debug.LogWarning("[UniMCP] AddMenuItem reflection failed: " + e.Message);
                return false;
            }
        }

        private static void TryRemoveMenuItem(string name)
        {
            try
            {
                var method = typeof(UnityEditor.Menu).GetMethod(
                    "RemoveMenuItem",
                    BindingFlags.NonPublic | BindingFlags.Static);
                method?.Invoke(null, new object[] { name });
            }
            catch { /* ignore */ }
        }

        private static void EnsureFallbackRegistered()
        {
            if (_fallbackRegistered)
                return;
            _fallbackRegistered = TryAddMenuItem(
                FallbackPath,
                500,
                ShowSkillPicker,
                HasAssetSelection);
        }

        private static void ShowSkillPicker()
        {
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
                menu.AddItem(new GUIContent(skill.name), false, () => RunSkillAsync(captured));
            }
            menu.ShowAsContext();
        }

        private static string EscapeMenuPath(string name)
        {
            return (name ?? "").Replace("/", "_").Replace("\\", "_");
        }

        private static bool HasAssetSelection()
        {
            return Selection.objects != null
                && Selection.objects.Any(o => o != null
                    && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));
        }

        private static void RunSkillAsync(UniMcpSkill skill)
        {
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

            UniMcpRunQueue.Enqueue(skill, paths);
        }
    }
}
