using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Chat
{
    [System.Serializable]
    public class ClaudeResponse
    {
        public string type;
        public string subtype;
        public string session_id;
        public string result;
        public bool is_error;
    }

    /// <summary>
    /// Claude Code CLI 서브프로세스 실행기.
    /// `--output-format json`으로 session_id와 응답 텍스트를 함께 수신하고 이후 호출은 `--resume`으로 컨텍스트 이어감
    /// </summary>
    public static class ClaudeProcess
    {
        public static async Task<ClaudeResponse> Send(
            string prompt,
            string workingDir,
            string resumeSessionId = null)
        {
            var psi = BuildProcessInfo(prompt, workingDir, resumeSessionId);
            using var process = new Process { StartInfo = psi };

            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Failed to launch Claude Code CLI. Ensure `claude` is installed and on PATH.\n" + e.Message, e);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = await outputTask;
            var error = await errorTask;
            await Task.Run(() => process.WaitForExit());

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"claude exited with code {process.ExitCode}.\n{error.Trim()}");

            var trimmed = output.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new InvalidOperationException("claude returned empty output.\n" + error.Trim());

            ClaudeResponse parsed;
            try { parsed = JsonUtility.FromJson<ClaudeResponse>(trimmed); }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Failed to parse Claude response JSON.\n" + e.Message +
                    "\nRaw output (truncated): " + Trunc(trimmed, 300), e);
            }

            if (parsed == null)
                throw new InvalidOperationException("Parsed Claude response is null.\nRaw: " + Trunc(trimmed, 300));

            if (parsed.is_error)
                throw new InvalidOperationException("Claude reported an error: " + (parsed.result ?? parsed.subtype ?? "unknown"));

            return parsed;
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

        private static ProcessStartInfo BuildProcessInfo(string prompt, string workingDir, string resumeId)
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

            var resumePart = string.IsNullOrEmpty(resumeId)
                ? ""
                : $" --resume {EscapeArg(resumeId)}";

            var claudeArgs =
                $"-p {escapedPrompt} " +
                $"--output-format json " +
                $"--append-system-prompt {escapedScope} " +
                $"--disallowedTools \"{DisallowedTools}\"" +
                addDirParts +
                resumePart;

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

        private static string Trunc(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
