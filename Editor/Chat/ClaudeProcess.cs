using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace UniMCP.Editor.Chat
{
    /// <summary>
    /// Claude Code CLI 서브프로세스 실행기.
    /// 단일 턴 전송 후 stdout을 텍스트로 수집한다
    /// </summary>
    public static class ClaudeProcess
    {
        public static async Task<string> Send(string prompt, string workingDir)
        {
            var psi = BuildProcessInfo(prompt, workingDir);
            using var process = new Process { StartInfo = psi };

            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Failed to launch Claude Code CLI. " +
                    "Ensure `claude` is installed and on PATH.\n" + e.Message, e);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = await outputTask;
            var error = await errorTask;
            await Task.Run(() => process.WaitForExit());

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"claude exited with code {process.ExitCode}.\n{error.Trim()}");

            return output.TrimEnd();
        }

        private const string ScopeSystemPrompt =
            "You are a Unity project assistant strictly scoped to the Unity project at the current working directory. " +
            "Hard rules:\n" +
            "1. Never read, list, write, or access any file or directory outside the working directory (the Unity project root).\n" +
            "2. Accessible paths are limited to the project's Assets/, Packages/, ProjectSettings/, Library/, UserSettings/ and their subdirectories.\n" +
            "3. If the user asks about paths like ~, /Users, /home, Desktop, C:/, or any absolute path outside this project, refuse briefly and state the scope limit.\n" +
            "4. Do not invoke WebFetch or WebSearch.\n" +
            "5. Treat this Unity project as the only context. Do not reference unrelated projects or external systems.";

        private const string DisallowedTools = "WebFetch,WebSearch";

        private static ProcessStartInfo BuildProcessInfo(string prompt, string workingDir)
        {
            var isWin = Application.platform == RuntimePlatform.WindowsEditor;
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var escapedPrompt = EscapeArg(prompt);
            var escapedScope = EscapeArg(ScopeSystemPrompt);
            var claudeArgs =
                $"-p {escapedPrompt} " +
                $"--append-system-prompt {escapedScope} " +
                $"--disallowedTools \"{DisallowedTools}\"";

            if (isWin)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c claude {claudeArgs}";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = $"-c \"claude {claudeArgs.Replace("\"", "\\\"")}\"";
            }
            return psi;
        }

        private static string EscapeArg(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
