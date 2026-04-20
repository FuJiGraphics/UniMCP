using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniMCP.Editor.Logging;
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
    /// stream-json 모드로 실행하여 진행 이벤트를 onProgress로 콜백한다
    /// </summary>
    public static class ClaudeProcess
    {
        public static async Task<ClaudeResponse> Send(
            string prompt,
            string workingDir,
            string resumeSessionId = null,
            Action<string> onProgress = null,
            CancellationToken cancellationToken = default,
            string modelOverride = null)
        {
            var psi = BuildProcessInfo(prompt, workingDir, resumeSessionId, modelOverride);
            using var process = new Process { StartInfo = psi };

            var startTime = DateTime.Now;
            // spawn 명령어는 너무 장황해서 파일·prompt·resume 만 요약 기록
            var shortArgs = Regex.Replace(psi.Arguments, "--append-system-prompt \"[^\"]*\"", "--append-system-prompt ...");
            UniMcpLogger.Info($"spawning: {TruncateArgs(shortArgs, 300)}");

            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Failed to launch Claude Code CLI. Ensure `claude` is installed and on PATH.\n" + e.Message, e);
            }

            using var cancelRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        KillProcessTree(process);
                }
                catch { /* already exited / access denied */ }
            });

            ClaudeResponse finalResult = null;
            var errorBuffer = new StringBuilder();
            int eventCount = 0;
            int toolUseCount = 0;

            var outputTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    eventCount++;

                    if (TryParseResult(line, out var result))
                    {
                        finalResult = result;
                        continue;
                    }

                    var toolMatch = Regex.Match(line, "\"type\":\"tool_use\"[^}]*?\"name\":\"([^\"]+)\"");

                    if (toolMatch.Success)
                    {
                        toolUseCount++;
                        var toolName = toolMatch.Groups[1].Value;
                        var inputMatch = Regex.Match(line, "\"input\":(\\{.*?\\})");
                        var inputSnippet = inputMatch.Success ? TruncateArgs(inputMatch.Groups[1].Value, 120) : "";
                        UniMcpLogger.Info($"tool_use #{toolUseCount}: {toolName} {inputSnippet}");
                    }

                    var progressText = ExtractProgressText(line);

                    if (!string.IsNullOrEmpty(progressText) && onProgress != null)
                        onProgress(progressText);
                }
            });

            var errorTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                    errorBuffer.AppendLine(line);
            });

            await Task.WhenAll(outputTask, errorTask);

            // 취소됐는데 Kill 이 실패한 경우에도 빠져나오도록 타임아웃 대기
            await Task.Run(() =>
            {
                if (!process.WaitForExit(5000))
                {
                    try { KillProcessTree(process); } catch { }
                    process.WaitForExit(2000);
                }
            });

            var duration = DateTime.Now - startTime;
            var stderrText = errorBuffer.ToString().Trim();

            UniMcpLogger.Info(
                $"claude finished: exit={process.ExitCode}  duration={duration.TotalSeconds:F1}s  " +
                $"events={eventCount}  tools={toolUseCount}" +
                (string.IsNullOrEmpty(stderrText) ? "" : $"\nstderr: {TruncateArgs(stderrText, 500)}"));

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Claude process was cancelled.", cancellationToken);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"claude exited with code {process.ExitCode}.\n{stderrText}");

            if (finalResult == null)
                throw new InvalidOperationException(
                    "claude did not emit a final `result` event.\n" + stderrText);

            if (finalResult.is_error)
                throw new InvalidOperationException(
                    "Claude reported an error: " + (finalResult.result ?? finalResult.subtype ?? "unknown"));

            return finalResult;
        }

        private const string ScopeSystemPrompt =
            "You are a Unity project assistant strictly scoped to the current Unity project and its manifest-linked dependencies. " +
            "Hard rules: " +
            "(1) Accessible paths: the Unity project root at the working directory (Assets/, Packages/, ProjectSettings/, Library/, UserSettings/, etc.) and any directory explicitly passed via --add-dir (manifest.json-referenced packages). " +
            "(2) Never read, list, write, or access paths outside these allowed roots. " +
            "(3) If asked about paths like ~, /Users, /home, Desktop, or any absolute path not in the allowed roots, refuse briefly and state the scope limit. " +
            "(4) Do not invoke WebFetch or WebSearch. " +
            "(5) Treat this Unity project as the only context. Do not reference unrelated projects or external systems. " +
            "(6) UniMCP provides shared helper tools (Python scripts, etc.) under its `Tools~/` directory. Before writing any ad-hoc analysis/parsing script for Unity assets, check that directory (try `../UniMCP/Tools~/`, `Packages/com.unimcp.core/Tools~/`, or Glob for `**/Tools~/README.md`). Read the `Tools~/README.md` to discover available tools, and prefer them over writing your own scripts. If a needed tool doesn't exist, report it instead of creating one-off scripts in the skill folder. " +
            "(7) NEVER create new files of any language (`.cs`, `.py`, `.sh`, `.js`, etc.) as part of a task unless the USER explicitly requested a new source file to be created in the codebase. This includes (especially): Unity Editor scripts (`[MenuItem]`, `EditorWindow`, `AssetDatabase.*`, `PrefabUtility.*`), one-off analysis scripts, and helper shell scripts. If you need to parse or analyze assets, use existing tools in `Tools~/` (rule 6) or run inline one-liners via Bash (`python -c \"...\"`, `grep`, `sed`). If an existing tool is missing, report it rather than creating a new script file. You are running in a Claude CLI subprocess; any code you write to disk will only pollute the user's repo unless it is the deliverable they asked for. " +
            "(8) Execution discipline — required for ALL skills: " +
            "  (a) If UniMCP `Tools~/` provides a single-shot tool that performs the whole task (e.g. `apply_convention.js` applies prefab naming convention end-to-end), call it ONCE and report its JSON output. Do not orchestrate multi-step workflows when a bundled tool exists. **All UniMCP tools are Node.js scripts** — invoke as `node ../UniMCP/Tools~/<tool>.js <args>`. Never try `python`/`python3`/`py` — no `.py` files exist. " +
            "  (b) After reading the tool's output, compute the full list of required changes in your head, then execute them. Do not perform additional reconnaissance between changes unless a change reveals new required information. " +
            "  (c) Budget: aim for ≤10 tool calls per target item. If you approach that limit, apply the changes you are certain of and stop — do not keep exploring. " +
            "  (d) Execute, don't just report. If the skill asks you to modify files, you MUST actually modify them (via Edit/Write/Bash). Ending with phrases like \"권장합니다\", \"다음 단계\", \"추가 검토 필요\", \"수작업으로 분석\", \"please verify\" while making zero write calls = failure. Either do the work or explicitly state you cannot (with the specific blocker). " +
            "  (e) If a tool call fails, retry once with a fix. Do not fall back to manual reconstruction of what the tool would have produced. " +
            "  (f) NEVER claim a file is missing, a tool doesn't exist, or a directory is empty unless you explicitly verified via a file-existence tool (Glob, Read, `ls`) in the CURRENT run. Command failure (non-zero exit, 'not found' message from a subshell, empty stdout) is NOT evidence that a file is absent — it usually means the runtime (python/py/node) couldn't be located or the command args were wrong. Before asserting absence, run `ls <dir>` or `Glob <pattern>` and cite that result. Writing \"analyze_prefab.py 도구가 없어서\" / \"tool not found\" / \"file missing\" without such verification is a hallucination and counts as skill failure. When in doubt, try the alternate runtime (e.g. `node <tool>.js` instead of `py <tool>.py`) before making claims.";

        private const string DisallowedTools = "WebFetch,WebSearch";

        private static string _cachedClaudePath;

        /// <summary>
        /// 실제 claude CLI 실행 파일 경로를 찾는다.
        /// Windows App Execution Alias (Microsoft Store stub)는 건너뛰고,
        /// where/which 가 실패하면 npm 기본 설치 경로를 직접 확인한다
        /// </summary>
        private static string ResolveClaudePath(bool isWin)
        {
            if (!string.IsNullOrEmpty(_cachedClaudePath))
                return _cachedClaudePath;

            var fromSearch = TryFindViaSearch(isWin);

            if (!string.IsNullOrEmpty(fromSearch))
            {
                _cachedClaudePath = fromSearch;
                UniMcpLogger.Info($"claude path resolved via {(isWin ? "where" : "which")}: {fromSearch}");
                return fromSearch;
            }

            var fromDefaults = TryFindInDefaultPaths(isWin);

            if (!string.IsNullOrEmpty(fromDefaults))
            {
                _cachedClaudePath = fromDefaults;
                UniMcpLogger.Info($"claude path resolved via default fallback: {fromDefaults}");
                return fromDefaults;
            }

            UniMcpLogger.Warn("claude path NOT resolved — falling back to bare `claude` (may hit Windows Store stub)");
            return null;
        }

        private static string TryFindViaSearch(bool isWin)
        {
            var command = isWin ? "where" : "which";
            var arg = isWin ? "claude.cmd" : "claude";

            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arg,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    }
                };
                p.Start();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    var path = line.Trim();

                    if (string.IsNullOrEmpty(path))
                        continue;

                    if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return path;
                }
            }
            catch { }

            return null;
        }

        private static string TryFindInDefaultPaths(bool isWin)
        {
            string[] candidates;

            if (isWin)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                candidates = new[]
                {
                    System.IO.Path.Combine(appData, "npm", "claude.cmd"),
                    System.IO.Path.Combine(programFiles, "nodejs", "claude.cmd"),
                };
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                candidates = new[]
                {
                    "/usr/local/bin/claude",
                    "/opt/homebrew/bin/claude",
                    System.IO.Path.Combine(home, ".nvm/versions/node/current/bin/claude"),
                };
            }

            foreach (var path in candidates)
            {
                if (System.IO.File.Exists(path))
                    return path;
            }

            return null;
        }

        private static ProcessStartInfo BuildProcessInfo(string prompt, string workingDir, string resumeId, string modelOverride = null)
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

            var model = string.IsNullOrEmpty(modelOverride) ? "sonnet" : modelOverride;
            var claudeArgs =
                $"-p {escapedPrompt} " +
                $"--model {model} " +
                $"--output-format stream-json " +
                $"--verbose " +
                $"--append-system-prompt {escapedScope} " +
                $"--disallowedTools \"{DisallowedTools}\" " +
                $"--permission-mode bypassPermissions" +
                addDirParts +
                resumePart;

            var claudePath = ResolveClaudePath(isWin);

            if (isWin)
            {
                psi.FileName = "cmd.exe";
                // Windows 경로는 백슬래시 그대로 두고 따옴표만 감싼다 (EscapeArg는 JSON용이라 부적합)
                var claudeRef = string.IsNullOrEmpty(claudePath) ? "claude" : $"\"{claudePath}\"";
                // cmd /c 가 선두·말미 따옴표를 벗기지 않도록 전체를 한 번 더 감싼다
                psi.Arguments = $"/c \"{claudeRef} {claudeArgs}\"";
            }
            else
            {
                psi.FileName = "/bin/sh";
                var claudeRef = string.IsNullOrEmpty(claudePath) ? "claude" : claudePath;
                psi.Arguments = $"-c \"{claudeRef} {claudeArgs.Replace("\"", "\\\"")}\"";
            }

            SanitizePathEnvironment(psi, isWin);
            return psi;
        }

        /// <summary>
        /// Windows App Execution Alias (WindowsApps) 경로를 PATH 에서 제거하고,
        /// Unity 프로세스 시작 이후 설치된 프로그램을 인식할 수 있도록 레지스트리에서 최신 PATH 를 읽어온다
        /// </summary>
        private static void SanitizePathEnvironment(ProcessStartInfo psi, bool isWin)
        {
            if (!isWin)
                return;

            var freshPath = ReadFreshPathFromRegistry() ?? Environment.GetEnvironmentVariable("PATH") ?? "";

            if (string.IsNullOrEmpty(freshPath))
                return;

            var cleaned = string.Join(
                ";",
                freshPath
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !p.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)));

            // 실제 Python 설치 디렉터리를 찾아 PATH 앞에 강제로 넣는다 (사용자 PATH 누락 대비)
            var pythonDirs = FindPythonDirs();
            if (pythonDirs.Count > 0)
                cleaned = string.Join(";", pythonDirs) + ";" + cleaned;

            psi.Environment["PATH"] = cleaned;
        }

        /// <summary>
        /// 흔한 Python 설치 위치를 점검해 존재하는 디렉터리를 반환 (우선순위 순).
        /// Launcher 디렉터리 + 최신 Python{version} 디렉터리
        /// </summary>
        private static List<string> FindPythonDirs()
        {
            var result = new List<string>();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates =
            {
                System.IO.Path.Combine(appData, "Programs", "Python", "Launcher"),
                System.IO.Path.Combine(appData, "Programs", "Python", "Python314"),
                System.IO.Path.Combine(appData, "Programs", "Python", "Python313"),
                System.IO.Path.Combine(appData, "Programs", "Python", "Python312"),
                System.IO.Path.Combine(appData, "Programs", "Python", "Python311"),
                System.IO.Path.Combine(programFiles, "Python314"),
                System.IO.Path.Combine(programFiles, "Python313"),
                System.IO.Path.Combine(programFiles, "Python312"),
                System.IO.Path.Combine(programFiles, "Python311"),
            };

            foreach (var dir in candidates)
            {
                if (System.IO.Directory.Exists(dir))
                    result.Add(dir);
            }

            return result;
        }

        /// <summary>
        /// 시스템·사용자 PATH 를 레지스트리에서 직접 읽어 결합.
        /// Unity 시작 이후 설치된 프로그램 (winget 등)도 즉시 반영됨
        /// </summary>
        private static string ReadFreshPathFromRegistry()
        {
            try
            {
                string machine;
                string user;

                using (var machineKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                {
                    machine = machineKey?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                }

                using (var userKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    user = userKey?.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                }

                var combined = Environment.ExpandEnvironmentVariables(
                    string.IsNullOrEmpty(user) ? machine : machine + ";" + user);

                return combined;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// stream-json 라인이 최종 result 이벤트인지 확인하고 파싱.
        /// </summary>
        private static bool TryParseResult(string line, out ClaudeResponse response)
        {
            response = null;

            if (!line.Contains("\"type\":\"result\""))
                return false;

            try
            {
                response = JsonUtility.FromJson<ClaudeResponse>(line);
            }
            catch
            {
                return false;
            }

            return response != null;
        }

        /// <summary>
        /// stream-json 이벤트에서 사용자에게 보여줄 진행 텍스트를 추출.
        /// tool_use는 도구 이름, text 메시지는 앞 60자만 가져온다
        /// </summary>
        private static string ExtractProgressText(string line)
        {
            var toolMatch = Regex.Match(line, "\"type\":\"tool_use\"[^}]*?\"name\":\"([^\"]+)\"");

            if (toolMatch.Success)
                return $"Using {toolMatch.Groups[1].Value}…";

            var textMatch = Regex.Match(line, "\"type\":\"text\"[^}]*?\"text\":\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"");

            if (textMatch.Success)
            {
                var text = Regex.Unescape(textMatch.Groups[1].Value).Replace("\n", " ").Trim();

                if (string.IsNullOrEmpty(text))
                    return null;

                return text.Length > 60 ? text.Substring(0, 60) + "…" : text;
            }

            return null;
        }

        private static string EscapeArg(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// 프로세스 트리 전체를 종료. .NET Standard 2.1 에서 `Kill(true)` 미지원인 경우 대비.
        /// Windows 에선 taskkill /T /F 사용, Unix 에선 기본 Kill
        /// </summary>
        private static void KillProcessTree(Process process)
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    using var killer = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/T /F /PID {process.Id}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        },
                    };
                    killer.Start();
                    killer.WaitForExit(1000);
                }
                else
                {
                    process.Kill();
                }
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
        }

        private static string TruncateArgs(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            var oneLine = s.Replace("\n", " ").Replace("\r", "");
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }
    }
}
