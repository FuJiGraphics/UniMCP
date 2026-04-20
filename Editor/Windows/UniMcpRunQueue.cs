using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Logging;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public enum JobState
    {
        Pending,
        Running,
        Success,
        Failed,
        Cancelled,
    }

    public class JobRecord
    {
        public Guid id;
        public UniMcpSkill skill;
        public List<string> targets;
        public JobState state;
        public DateTime startedAt;
        public DateTime? finishedAt;
        public string progressText;
        public string resultText;
        public string errorMessage;
        public int progressId;
        public CancellationTokenSource cts;
        public Action<ClaudeResponse> onSuccess;
        public Action<Exception> onFailure;
        /// <summary>
        /// true 면 `/skill` 슬래시 커맨드 대신 `skill.prompt` 를 사용자 메시지로 직접 전달.
        /// UniMCP 패키지 내장 스킬용 (프로젝트 `.claude/skills` 에 파일을 쓰지 않는다)
        /// </summary>
        public bool isBuiltin;

        /// <summary>
        /// 작업 단위 로그 파일 경로. 세팅되면 시작·진행·완료 이벤트를 여기 append.
        /// 비어있으면 파일 로그 미기록
        /// </summary>
        public string logPath;

        /// <summary>
        /// Claude 모델 오버라이드. 비어있으면 기본(sonnet).
        /// 복잡한 추론이 필요한 빌트인(예: 프리팹 생성)은 "opus" 로 지정
        /// </summary>
        public string modelOverride;
    }

    /// <summary>
    /// 스킬 실행 요청을 병렬 큐로 처리한다.
    /// UniMcpSettings.MaxConcurrentJobs 만큼 동시 실행하며, 동일 타겟을 공유하는 작업은 항상 직렬 처리한다
    /// </summary>
    public static class UniMcpRunQueue
    {
        private static readonly List<JobRecord> _pending = new();
        private static readonly List<JobRecord> _running = new();
        private static readonly List<JobRecord> _history = new();
        private static double _lastHeartbeatTime;
        private static bool _heartbeatHooked;

        public static event Action QueueChanged;

        public static int QueuedCount => _pending.Count;
        public static bool IsBusy => _running.Count > 0;
        public static IReadOnlyList<JobRecord> History => _history;
        public static UniMcpSkill RunningSkill => _running.Count > 0 ? _running[0].skill : null;
        public static IReadOnlyList<UniMcpSkill> RunningSkills => _running.Select(j => j.skill).ToList();

        public static Guid Enqueue(
            UniMcpSkill skill,
            IEnumerable<string> targetPaths,
            Action<ClaudeResponse> onSuccess = null,
            Action<Exception> onFailure = null,
            bool isBuiltin = false,
            string logPath = null,
            string modelOverride = null)
        {
            if (skill == null)
                return Guid.Empty;

            var targets = (targetPaths ?? Array.Empty<string>()).ToList();

            if (targets.Count == 0)
                return Guid.Empty;

            var job = new JobRecord
            {
                id = Guid.NewGuid(),
                skill = skill,
                targets = targets,
                state = JobState.Pending,
                startedAt = DateTime.Now,
                cts = new CancellationTokenSource(),
                onSuccess = onSuccess,
                onFailure = onFailure,
                isBuiltin = isBuiltin,
                logPath = logPath,
                modelOverride = modelOverride,
            };

            _pending.Add(job);
            _history.Add(job);
            QueueChanged?.Invoke();

            TryDispatch();

            return job.id;
        }

        public static bool Cancel(Guid id)
        {
            var running = _running.FirstOrDefault(j => j.id == id);

            if (running != null)
            {
                try { running.cts.Cancel(); }
                catch { }
                return true;
            }

            var pending = _pending.FirstOrDefault(j => j.id == id);

            if (pending != null)
            {
                pending.state = JobState.Cancelled;
                pending.finishedAt = DateTime.Now;
                _pending.Remove(pending);
                QueueChanged?.Invoke();
                return true;
            }

            return false;
        }

        public static bool RemoveFromHistory(Guid id)
        {
            var job = _history.FirstOrDefault(j => j.id == id);

            if (job == null)
                return false;

            if (job.state == JobState.Pending || job.state == JobState.Running)
                return false;

            _history.Remove(job);
            QueueChanged?.Invoke();
            return true;
        }

        public static void ClearFinishedHistory()
        {
            _history.RemoveAll(j => j.state != JobState.Pending && j.state != JobState.Running);
            QueueChanged?.Invoke();
        }

        /// <summary>
        /// pending 큐에서 실행 가능한 작업을 찾아 동시 실행 한도만큼 디스패치한다.
        /// 같은 타겟을 이미 실행 중인 작업이 있으면 충돌을 피하기 위해 대기시킨다
        /// </summary>
        private static void TryDispatch()
        {
            var maxConcurrent = UniMcpSettings.instance.MaxConcurrentJobs;

            while (_running.Count < maxConcurrent)
            {
                var next = _pending.FirstOrDefault(j => !ConflictsWithRunning(j));

                if (next == null)
                    break;

                _pending.Remove(next);
                _running.Add(next);
                _ = RunJob(next);
            }

            if (_running.Count > 0)
                StartHeartbeat();
            else
                StopHeartbeat();
        }

        private static bool ConflictsWithRunning(JobRecord job)
        {
            foreach (var r in _running)
            {
                foreach (var t in job.targets)
                {
                    if (r.targets.Contains(t))
                        return true;
                }
            }

            return false;
        }

        private static async Task RunJob(JobRecord job)
        {
            job.state = JobState.Running;
            job.startedAt = DateTime.Now;
            job.progressId = Progress.Start(
                $"UniMCP · {job.skill.name}",
                BuildDescription(job));

            Progress.RegisterCancelCallback(job.progressId, () =>
            {
                try { job.cts.Cancel(); }
                catch { }
                return true;
            });

            QueueChanged?.Invoke();

            UniMcpLogger.Info($"시작: {job.skill.name} | targets={string.Join(", ", job.targets)}");

            WriteJobLogHeader(job);
            PrefabHook.PrefabHookExecutor.ActiveLogPath = job.logPath;
            PrefabHook.PrefabHookExecutor.ResetHookCounter();
            UniMcpLogger.ActiveLogPath = job.logPath;

            OpenTasksWindow();

            try
            {
                var invocation = SkillStore.GetInvocationName(job.skill.name);
                var prompt = $"/{invocation} Targets: {string.Join(", ", job.targets)}";
                var cwd = Path.GetDirectoryName(Application.dataPath);

                var response = await ClaudeProcess.Send(prompt, cwd, null,
                    progressText =>
                    {
                        job.progressText = progressText;
                        AppendJobLog(job, "PROGRESS  " + progressText);
                        EditorApplication.delayCall += () =>
                        {
                            try
                            {
                                if (job.progressId != 0 && Progress.Exists(job.progressId))
                                    Progress.SetDescription(job.progressId, progressText);
                            }
                            catch { }
                            QueueChanged?.Invoke();
                        };
                    },
                    job.cts.Token,
                    modelOverride: job.modelOverride);

                job.state = JobState.Success;
                job.finishedAt = DateTime.Now;
                job.resultText = response.result ?? "";

                AppendJobLog(job, $"JOB END success ({(job.finishedAt - job.startedAt)?.TotalSeconds:F1}s)");
                if (!string.IsNullOrWhiteSpace(response.result))
                    AppendJobLog(job, "RESULT\n" + response.result);

                // 스킬이 프리팹을 외부에서 수정했을 수 있으니 타겟 강제 리임포트
                foreach (var target in job.targets)
                {
                    try
                    {
                        AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceUpdate);
                    }
                    catch { }
                }

                UniMcpLogger.Info($"{job.skill.name} 완료");

                if (!string.IsNullOrWhiteSpace(response.result))
                    UniMcpLogger.Info($"result:\n{response.result}");

                Progress.Finish(job.progressId);
                SafeInvoke(() => job.onSuccess?.Invoke(response));
            }
            catch (OperationCanceledException)
            {
                job.state = JobState.Cancelled;
                job.finishedAt = DateTime.Now;
                UniMcpLogger.Info($"{job.skill.name} 취소됨");
                AppendJobLog(job, "JOB END cancelled");
                Progress.Finish(job.progressId, Progress.Status.Canceled);
            }
            catch (Exception e)
            {
                job.state = JobState.Failed;
                job.finishedAt = DateTime.Now;
                job.errorMessage = e.Message;
                UniMcpLogger.Error($"{job.skill.name} 실패: {e.Message}");
                AppendJobLog(job, "JOB END failed: " + e.Message);
                Progress.Finish(job.progressId, Progress.Status.Failed);
                SafeInvoke(() => job.onFailure?.Invoke(e));
            }

            if (PrefabHook.PrefabHookExecutor.ActiveLogPath == job.logPath)
                PrefabHook.PrefabHookExecutor.ActiveLogPath = null;
            if (UniMcpLogger.ActiveLogPath == job.logPath)
                UniMcpLogger.ActiveLogPath = null;

            _running.Remove(job);
            QueueChanged?.Invoke();

            TryDispatch();

            if (_running.Count == 0 && _pending.Count == 0)
                AssetDatabase.Refresh();
        }

        private static void WriteJobLogHeader(JobRecord job)
        {
            if (string.IsNullOrEmpty(job.logPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(job.logPath));
                var header =
                    $"====================================================\n" +
                    $"JOB    {job.id}\n" +
                    $"SKILL  {job.skill.name}" + (job.isBuiltin ? " (builtin)" : "") + "\n" +
                    $"START  {job.startedAt:yyyy-MM-dd HH:mm:ss}\n" +
                    $"TARGETS\n" +
                    string.Join("", job.targets.Select(t => $"  - {t}\n")) +
                    $"====================================================\n";
                File.WriteAllText(job.logPath, header);
            }
            catch (Exception e) { UniMcpLogger.Warn("로그 헤더 기록 실패: " + e.Message); }
        }

        private static void AppendJobLog(JobRecord job, string line)
        {
            if (string.IsNullOrEmpty(job.logPath)) return;
            try
            {
                File.AppendAllText(job.logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
            }
            catch { }
        }

        [MenuItem("UniMCP/Background Tasks")]
        private static void OpenTasksMenu()
        {
            OpenTasksWindow();
        }

        /// <summary>
        /// 큐 상태가 꼬였을 때 (좀비 _running, 오래 중단된 작업 등) 강제로 전부 정리.
        /// Settings UI 에서 호출
        /// </summary>
        public static void ResetQueuePublic() => ResetQueueMenu();

        private static void ResetQueueMenu()
        {
            foreach (var job in _running)
            {
                try { job.cts?.Cancel(); } catch { }
                try
                {
                    if (job.progressId != 0 && Progress.Exists(job.progressId))
                        Progress.Finish(job.progressId, Progress.Status.Canceled);
                }
                catch { }
                job.state = JobState.Cancelled;
                job.finishedAt = DateTime.Now;
            }

            foreach (var job in _pending)
            {
                job.state = JobState.Cancelled;
                job.finishedAt = DateTime.Now;
            }

            _running.Clear();
            _pending.Clear();
            StopHeartbeat();
            QueueChanged?.Invoke();
            UniMcpLogger.Info("Run queue 강제 리셋");
        }

        private static void OpenTasksWindow()
        {
            try { EditorApplication.ExecuteMenuItem("Window/General/Progress"); }
            catch { }
        }

        private static string BuildDescription(JobRecord job)
        {
            return job.targets.Count == 1
                ? job.targets[0]
                : $"{job.targets.Count} targets";
        }

        private static void StartHeartbeat()
        {
            if (_heartbeatHooked)
                return;

            _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Heartbeat;
            _heartbeatHooked = true;
        }

        private static void StopHeartbeat()
        {
            if (!_heartbeatHooked)
                return;

            EditorApplication.update -= Heartbeat;
            _heartbeatHooked = false;
        }

        private static void Heartbeat()
        {
            var now = EditorApplication.timeSinceStartup;

            if (now - _lastHeartbeatTime < 1.0)
                return;

            _lastHeartbeatTime = now;

            foreach (var job in _running)
            {
                if (job.progressId == 0)
                    continue;

                try
                {
                    if (Progress.Exists(job.progressId))
                        Progress.Report(job.progressId, -1f);
                }
                catch { }
            }
        }

        private static void SafeInvoke(Action a)
        {
            try { a?.Invoke(); }
            catch (Exception e) { UniMcpLogger.Exception(e); }
        }
    }
}
