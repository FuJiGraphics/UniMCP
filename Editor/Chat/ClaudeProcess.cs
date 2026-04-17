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

            var escaped = EscapeArg(prompt);
            if (isWin)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c claude -p {escaped}";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.Arguments = $"-c \"claude -p {escaped}\"";
            }
            return psi;
        }

        private static string EscapeArg(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
