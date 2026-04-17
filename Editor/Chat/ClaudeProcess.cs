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
            "You are a Unity project assistant strictly scoped to the current Unity project and its manifest-linked dependencies. " +
            "Hard rules:\n" +
            "1. Accessible paths: the Unity project root at the working directory (Assets/, Packages/, ProjectSettings/, Library/, UserSettings/, etc.) and any directory explicitly passed via --add-dir (these are packages referenced by the project's manifest.json).\n" +
            "2. Never read, list, write, or access paths outside these allowed roots.\n" +
            "3. If asked about paths like ~, /Users, /home, Desktop, or any absolute path not in the allowed roots, refuse briefly and state the scope limit.\n" +
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

            var addDirParts = new StringBuilder();
            foreach (var dir in ManifestResolver.GetExternalPackageDirs(workingDir))
                addDirParts.Append(" --add-dir ").Append(EscapeArg(dir));

            var claudeArgs =
                $"-p {escapedPrompt} " +
                $"--append-system-prompt {escapedScope} " +
                $"--disallowedTools \"{DisallowedTools}\"" +
                addDirParts;

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
