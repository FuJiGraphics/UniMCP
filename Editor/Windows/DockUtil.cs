using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// 같은 DockArea에 EditorWindow를 탭으로 붙이기 위한 Reflection 기반 헬퍼.
    /// Unity 내부 DockArea.AddTab API에 의존하므로 버전 호환성이 깨질 경우 플로팅으로 fallback된다
    /// </summary>
    internal static class DockUtil
    {
        public static T FindFirstOpen<T>() where T : EditorWindow
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            foreach (var w in all)
                if (w != null)
                    return w;
            return null;
        }

        public static bool TryDockNextTo(EditorWindow anchor, EditorWindow target)
        {
            if (anchor == null || target == null)
                return false;

            try
            {
                var parentField = typeof(EditorWindow).GetField(
                    "m_Parent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var dockArea = parentField?.GetValue(anchor);
                if (dockArea == null)
                    return false;

                var addTab = dockArea.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "AddTab" &&
                        m.GetParameters().Length >= 1 &&
                        m.GetParameters()[0].ParameterType == typeof(EditorWindow));

                if (addTab == null)
                    return false;

                var parms = addTab.GetParameters();
                var args = parms.Length == 1
                    ? new object[] { target }
                    : new object[] { target, true };

                addTab.Invoke(dockArea, args);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UniMCP] Dock attempt failed, falling back to floating. " + e.Message);
                return false;
            }
        }
    }
}
