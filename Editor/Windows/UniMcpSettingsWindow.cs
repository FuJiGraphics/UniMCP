using System;
using System.Diagnostics;
using UniMCP.Editor.Logging;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpSettingsWindow : EditorWindow
    {
        private int _maxConcurrentBuffer;
        private Vector2 _scroll;

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
                w.minSize = new Vector2(520, 240);
                w.Show();
                return;
            }

            var window = CreateInstance<UniMcpSettingsWindow>();
            window.titleContent = new GUIContent("UniMCP Settings");
            window.minSize = new Vector2(520, 240);

            if (!DockUtil.TryDockNextTo(chat, window))
                window.Show();
        }

        private void OnEnable()
        {
            _maxConcurrentBuffer = UniMcpSettings.instance.MaxConcurrentJobs;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeader();
            DrawExecutionSettings();
            EditorGUILayout.Space(16);
            DrawToolchain();
            EditorGUILayout.Space(16);
            DrawResetSection();
            EditorGUILayout.EndScrollView();
        }

        // ---- Reset Section ----

        private void DrawResetSection()
        {
            EditorGUILayout.LabelField("Reset / Maintenance", EditorStyles.boldLabel);

            if (GUILayout.Button("Reset Run Queue", GUILayout.Height(22)))
            {
                UniMcpRunQueue.ResetQueuePublic();
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("UniMCP Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ProjectSettings/UniMcpSettings.asset (git 커밋 → 팀 공유).",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(10);
        }

        private void DrawExecutionSettings()
        {
            EditorGUILayout.LabelField("스킬 실행", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "동시에 실행할 최대 스킬 개수. 1이면 순차 실행. 동일 타겟을 공유하는 작업은 충돌 방지를 위해 항상 직렬 처리됩니다.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("동시 실행 한도", GUILayout.Width(120));
                _maxConcurrentBuffer = EditorGUILayout.IntSlider(_maxConcurrentBuffer, 1, 8);
            }

            EditorGUILayout.Space(4);

            var dirty = _maxConcurrentBuffer != UniMcpSettings.instance.MaxConcurrentJobs;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                GUI.enabled = dirty;

                if (GUILayout.Button(dirty ? "Save *" : "Save", GUILayout.Width(100)))
                    UniMcpSettings.instance.MaxConcurrentJobs = _maxConcurrentBuffer;

                GUI.enabled = true;
            }
        }

        private void DrawToolchain()
        {
            EditorGUILayout.LabelField("툴체인", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "UniMCP 공용 스크립트(Tools~) 실행에 필요한 런타임. 미설치 시 skill 실행 속도 저하 및 Microsoft Store 팝업 유발.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            var pythonStatus = DetectPython();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Python", GUILayout.Width(80));
                var prevColor = GUI.color;
                GUI.color = pythonStatus.ok ? Color.green : Color.red;
                EditorGUILayout.LabelField(pythonStatus.label);
                GUI.color = prevColor;

                if (!pythonStatus.ok)
                {
                    if (GUILayout.Button("Install via winget", GUILayout.Width(140)))
                        InstallPythonViaWinget();

                    if (GUILayout.Button("다운로드 페이지", GUILayout.Width(110)))
                        Application.OpenURL("https://www.python.org/downloads/");
                }
            }
        }

        private struct PythonStatus { public bool ok; public string label; }

        /// <summary>
        /// 시스템에 실제 Python 이 설치돼 있는지 확인. WindowsApps stub 은 미설치로 간주
        /// </summary>
        private PythonStatus DetectPython()
        {
            try
            {
                var psi = new ProcessStartInfo("where", "python")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                using var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    if (trimmed.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    return new PythonStatus { ok = true, label = trimmed };
                }
            }
            catch { }

            return new PythonStatus { ok = false, label = "미설치 또는 WindowsApps stub 만 존재" };
        }

        /// <summary>
        /// winget 으로 Python 3.12 설치 시도. winget 미설치 시 에러 로그
        /// </summary>
        private void InstallPythonViaWinget()
        {
            try
            {
                var psi = new ProcessStartInfo("winget", "install -e --id Python.Python.3.12 --accept-source-agreements --accept-package-agreements")
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                };

                Process.Start(psi);
                UniMcpLogger.Info("winget 으로 Python 3.12 설치 시작. 새 창에서 진행 상태 확인.");
            }
            catch (Exception e)
            {
                UniMcpLogger.Error("winget 실행 실패: " + e.Message);
                EditorUtility.DisplayDialog(
                    "winget 실행 실패",
                    "winget 을 사용할 수 없습니다. 다운로드 페이지에서 수동 설치하거나,\n" +
                    "Windows 11 에선 앱 설치 프로그램(App Installer) 을 Store 에서 업데이트하세요.\n\n" +
                    e.Message,
                    "OK");
            }
        }
    }
}
