using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Settings;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    /// <summary>
    /// 스킬 실행 요청을 전역 직렬 큐로 처리한다.
    /// 동시 실행 1건만 허용되어 같은 파일 편집 충돌·rate limit을 회피한다.
    /// Progress API로 개별 작업 상태를 노출하고 QueueChanged 이벤트로 UI가 반응한다
    /// </summary>
    public static class UniMcpRunQueue
    {
        private class Job
        {
            public UniMcpSkill skill;
            public List<string> targets;
            public Action<ClaudeResponse> onSuccess;
            public Action<Exception> onFailure;
        }

        private static readonly Queue<Job> _pending = new();
        private static Job _running;
        private static int _runningProgressId;

        public static event Action QueueChanged;

        public static int QueuedCount => _pending.Count;
        public static bool IsBusy => _running != null;
        public static UniMcpSkill RunningSkill => _running?.skill;

        public static void Enqueue(
            UniMcpSkill skill,
            IEnumerable<string> targetPaths,
            Action<ClaudeResponse> onSuccess = null,
            Action<Exception> onFailure = null)
        {
            if (skill == null)
                return;

            var targets = (targetPaths ?? Array.Empty<string>()).ToList();
            if (targets.Count == 0)
                return;

            var job = new Job
            {
                skill = skill,
                targets = targets,
                onSuccess = onSuccess,
                onFailure = onFailure,
            };

            _pending.Enqueue(job);
            QueueChanged?.Invoke();
            UpdateRunningProgressDescription();

            if (_running == null)
                _ = WorkerLoop();
        }

        private static async Task WorkerLoop()
        {
            while (_pending.Count > 0)
            {
                _running = _pending.Dequeue();
                _runningProgressId = Progress.Start(
                    $"UniMCP · {_running.skill.name}",
                    BuildDescription(_running));
                QueueChanged?.Invoke();

                try
                {
                    var invocation = SkillStore.GetInvocationName(_running.skill.name);
                    var prompt = $"/{invocation} Targets: {string.Join(", ", _running.targets)}";
                    var cwd = Path.GetDirectoryName(Application.dataPath);

                    var response = await ClaudeProcess.Send(prompt, cwd, null);

                    Debug.Log(
                        $"[UniMCP] {_running.skill.name} 완료\n" +
                        $"Targets: {string.Join(", ", _running.targets)}\n---\n" +
                        (response.result ?? ""));

                    Progress.Finish(_runningProgressId);
                    SafeInvoke(() => _running.onSuccess?.Invoke(response));
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UniMCP] {_running.skill.name} 실패: {e.Message}");
                    Progress.Finish(_runningProgressId, Progress.Status.Failed);
                    SafeInvoke(() => _running.onFailure?.Invoke(e));
                }

                _running = null;
                _runningProgressId = 0;
                QueueChanged?.Invoke();
            }

            AssetDatabase.Refresh();
        }

        private static string BuildDescription(Job job)
        {
            var q = _pending.Count;
            return q > 0
                ? $"Targets: {job.targets.Count}  |  Queued: {q}"
                : $"Targets: {job.targets.Count}";
        }

        private static void UpdateRunningProgressDescription()
        {
            if (_running == null || _runningProgressId == 0)
                return;
            try { Progress.SetDescription(_runningProgressId, BuildDescription(_running)); }
            catch { /* progress may already be finished */ }
        }

        private static void SafeInvoke(Action a)
        {
            try { a?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
