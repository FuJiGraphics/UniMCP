// JsonUtility 가 채우는 필드는 컴파일러가 미할당으로 오진
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.IO;
using UniMCP.Editor.Chat;
using UniMCP.Editor.Logging;
using UniMCP.Editor.Settings;
using UniMCP.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// 3-agent 프리팹 생성 파이프라인.
    ///   1) Analyzer  — 이미지 → TreeSpec JSON
    ///   2) Generator — TreeSpec → 프리팹 (C#, 결정론적)
    ///   3) Reviewer  — 레퍼런스 이미지 + 스크린샷 + TreeSpec → 수정 TreeSpec (반복)
    /// 최대 MaxIterations 회까지 루프, Reviewer 가 ""ok"" 판정하면 조기 종료
    /// </summary>
    public class PrefabGenerationPipeline
    {
        private const int MaxIterations = 2;

        private readonly string _imagePath;
        private readonly string _manifestPath;
        private readonly string _logPath;
        private readonly string _jobIdShort;
        private readonly Action<string> _onStatus;
        private readonly Action<Result> _onComplete;

        /// <summary>
        /// Analyzer 가 TreeSpec 을 뽑은 직후 호출. 유저가 편집할 기회. 구독자가 ApproveTreeSpec() 부를 때까지 파이프라인은 대기.
        /// 구독자가 없으면 자동으로 Generator 진행
        /// </summary>
        public Action<string> OnTreeSpecReady;

        /// <summary>
        /// Reviewer 가 ok 판정한 후 호출. 유저가 최종 수락(AcceptFinal) 또는 재시도(RefineWithHint) 선택할 수 있도록 대기.
        /// 구독자 없으면 자동 종료
        /// </summary>
        public Action<FinalState> OnFinalReady;

        public class FinalState
        {
            public string prefabPath;
            public string screenshotPath;
            public string treeSpecJson;
            public string reviewerSummary;
            public int iterationsUsed;
        }

        private bool _waitingForApproval;
        private bool _waitingForFinalAccept;
        private string _treeSpecJsonPath;
        private string _screenshotPath;
        private string _prefabPath;
        private string _currentTreeSpec;
        private int _iteration;
        private bool _cancelled;
        private Guid _currentJobId;

        public Guid CurrentJobId => _currentJobId;

        public class Result
        {
            public bool success;
            public string prefabPath;
            public int iterationsUsed;
            public string error;
            public string summary;
        }

        public PrefabGenerationPipeline(
            string imagePath, string manifestPath, string logPath, string jobIdShort,
            Action<string> onStatus, Action<Result> onComplete)
        {
            _imagePath     = imagePath;
            _manifestPath  = manifestPath;
            _logPath       = logPath;
            _jobIdShort    = jobIdShort;
            _onStatus      = onStatus;
            _onComplete    = onComplete;
        }

        public void Start() => EnqueueAnalyzer();

        public bool IsWaitingForApproval => _waitingForApproval;
        public bool IsWaitingForFinalAccept => _waitingForFinalAccept;

        /// <summary>
        /// Analyzer 대기 중일 때 유저가 편집한 TreeSpec 을 전달해 파이프라인을 재개
        /// </summary>
        public void ApproveTreeSpec(string editedTreeSpec)
        {
            if (!_waitingForApproval || _cancelled) return;
            _waitingForApproval = false;
            if (!string.IsNullOrEmpty(editedTreeSpec))
            {
                _currentTreeSpec = editedTreeSpec;
                AppendLog("USER_EDITED_TREE_SPEC\n" + Truncate(_currentTreeSpec, 4000));
            }
            GenerateAndScreenshot(initial: true);
        }

        /// <summary>
        /// 최종 수락: 유저가 결과에 만족. 파이프라인 Complete.
        /// </summary>
        public void AcceptFinal()
        {
            if (!_waitingForFinalAccept || _cancelled) return;
            _waitingForFinalAccept = false;
            AppendLog("USER_ACCEPTED_FINAL");
            Complete(true, $"유저 수락 완료 (iter {_iteration})");
        }

        /// <summary>
        /// 최종 확인 단계에서 유저가 크기 리뷰를 명시적으로 요청. Reviewer 호출 후 patch 적용 + 재생성.
        /// `extraHint` 가 있으면 manifest 의 userHint 를 갱신해 Reviewer 가 추가 가이드로 반영.
        /// Reviewer 가 ""ok"" 하면 다시 OnFinalReady 로 복귀. 반복 한도 초과 시 자동 종료
        /// </summary>
        public void RunSizeReview(string extraHint = null)
        {
            if (!_waitingForFinalAccept || _cancelled) return;
            _waitingForFinalAccept = false;
            if (!string.IsNullOrWhiteSpace(extraHint))
                UpdateManifestHint(extraHint);
            // EnqueueReviewer 내부에서 ++_iteration 을 하므로 현재 반복을 유지하기 위해 미리 감소
            _iteration--;
            AppendLog("USER_REQUESTED_SIZE_REVIEW" + (string.IsNullOrWhiteSpace(extraHint) ? "" : "\n" + extraHint));
            EnqueueReviewer();
        }

        /// <summary>
        /// 결과가 마음에 안 들어 힌트를 추가/변경해 재시도. manifest 의 userHint 를 업데이트하고 Analyzer 부터 다시 시작
        /// </summary>
        public void RefineWithHint(string additionalHint)
        {
            if (!_waitingForFinalAccept || _cancelled) return;
            _waitingForFinalAccept = false;
            UpdateManifestHint(additionalHint);
            _iteration = 0;
            _currentTreeSpec = null;
            AppendLog("USER_REFINE_WITH_HINT\n" + (additionalHint ?? ""));
            EnqueueAnalyzer();
        }

        private void UpdateManifestHint(string newHint)
        {
            try
            {
                var text = File.ReadAllText(_manifestPath);
                var m = JsonUtility.FromJson<HintManifestView>(text);
                m.userHint = newHint ?? "";
                File.WriteAllText(_manifestPath, JsonUtility.ToJson(m, prettyPrint: true));
            }
            catch (Exception e) { UniMcpLogger.Warn("manifest userHint 업데이트 실패: " + e.Message); }
        }

        [Serializable]
        private class HintManifestView
        {
            public string jobId;
            public string imagePath;
            public string outputFolder;
            public string userHint;
            public PrefabMetadataExtractor.PrefabMeta[] referencePrefabs;
            public string referenceFont;
            public string[] createdAssets;
        }

        public void Cancel()
        {
            _cancelled = true;
            if (_currentJobId != Guid.Empty)
            {
                try { UniMcpRunQueue.Cancel(_currentJobId); } catch { }
            }
        }

        // ---- Phase 1: Analyzer ----

        private void EnqueueAnalyzer()
        {
            if (_cancelled) return;
            Status("[1/3] 이미지 분석 중 (Analyzer)…");

            var skill = BuiltinSkills.GetPrefabGenSkill();
            _currentJobId = UniMcpRunQueue.Enqueue(skill, new[] { _imagePath, _manifestPath },
                onSuccess: resp =>
                {
                    if (_cancelled) return;
                    _currentTreeSpec = TreeGenerator.ExtractJsonBlock(resp.result ?? "");
                    AppendLog("ANALYZER_RESULT\n" + Truncate(_currentTreeSpec, 4000));

                    // 유저 approval 대기: 구독자 있으면 여기서 멈춤
                    if (OnTreeSpecReady != null)
                    {
                        _waitingForApproval = true;
                        Status("분석 완료 — 유저 승인 대기. Approve 또는 편집 후 Continue 클릭");
                        OnTreeSpecReady.Invoke(_currentTreeSpec);
                        return;
                    }

                    // 구독자 없으면 바로 진행
                    GenerateAndScreenshot(initial: true);
                },
                onFailure: ex =>
                {
                    if (_cancelled) return;
                    Complete(false, "Analyzer 실패: " + ex.Message);
                },
                isBuiltin: true,
                logPath: _logPath,
                modelOverride: "opus");

            if (_currentJobId == Guid.Empty)
                Complete(false, "Analyzer 큐 예약 실패");
        }

        // ---- Phase 2: Generate + Screenshot ----

        private void GenerateAndScreenshot(bool initial)
        {
            if (_cancelled) return;
            if (string.IsNullOrEmpty(_currentTreeSpec))
            {
                Complete(false, "TreeSpec 비어있음");
                return;
            }

            Status((initial ? "[2/3] " : "") + $"프리팹 생성 (iter {_iteration + 1})…");

            TreeGenerator.Result genResult;
            try { genResult = TreeGenerator.Generate(_currentTreeSpec); }
            catch (Exception e)
            {
                Complete(false, "TreeGenerator 예외: " + e.Message);
                return;
            }

            if (!genResult.success)
            {
                Complete(false, "TreeGenerator 실패: " + genResult.error);
                return;
            }

            _prefabPath = genResult.prefabPath;
            AppendLog($"GENERATOR_OK  {_prefabPath}");
            AppendCreatedAssetToManifest(_prefabPath);

            // TreeSpec 을 파일로 저장 (Reviewer 가 읽음)
            SaveTreeSpecToFile();

            // 스크린샷
            var screenshotDir = Path.Combine(Path.GetDirectoryName(_logPath), $"{_jobIdShort}-screenshots");
            Directory.CreateDirectory(screenshotDir);
            _screenshotPath = Path.Combine(screenshotDir, $"iter{_iteration + 1}.png").Replace('\\', '/');

            var shot = PrefabScreenshot.Capture(_prefabPath, _screenshotPath);
            if (!shot.success)
            {
                AppendLog("SCREENSHOT_FAIL  " + shot.error);
                // 스크린샷 실패해도 Reviewer 는 TreeSpec + 이미지로 진행
                _screenshotPath = null;
            }
            else
            {
                AppendLog($"SCREENSHOT_OK  {_screenshotPath}");
            }

            // Reviewer 자동 호출 제거 — 유저가 결과 본 뒤 명시적으로 결정
            // OnFinalReady 로 즉시 대기 상태 진입
            _iteration++;
            if (OnFinalReady != null)
            {
                _waitingForFinalAccept = true;
                Status($"생성 완료 (iter {_iteration}) — 유저 확인 대기");
                OnFinalReady.Invoke(new FinalState
                {
                    prefabPath = _prefabPath,
                    screenshotPath = _screenshotPath,
                    treeSpecJson = _currentTreeSpec,
                    reviewerSummary = null,
                    iterationsUsed = _iteration,
                });
                return;
            }

            Complete(true, $"생성 완료 (iter {_iteration})");
        }

        // ---- Phase 3: Reviewer ----

        private void EnqueueReviewer()
        {
            if (_cancelled) return;

            _iteration++;
            if (_iteration > MaxIterations)
            {
                Complete(true, $"최대 반복({MaxIterations}) 도달 — 현재 상태로 종료");
                return;
            }

            Status($"[3/3] 크기 검토 중 (iter {_iteration}/{MaxIterations})…");

            var skill = BuiltinSkills.GetPrefabReviewSkill();
            var targets = new List<string> { _imagePath };
            if (!string.IsNullOrEmpty(_screenshotPath)) targets.Add(_screenshotPath);
            targets.Add(_treeSpecJsonPath);
            // manifest 전달 → Reviewer 가 userHint 를 읽어 추가 힌트로 반영
            if (!string.IsNullOrEmpty(_manifestPath)) targets.Add(_manifestPath);

            _currentJobId = UniMcpRunQueue.Enqueue(skill, targets,
                onSuccess: resp =>
                {
                    if (_cancelled) return;
                    HandleReviewerResult(resp.result ?? "");
                },
                onFailure: ex =>
                {
                    if (_cancelled) return;
                    AppendLog("REVIEWER_FAIL  " + ex.Message);
                    // Review 실패해도 앞서 생성된 결과는 유지
                    Complete(true, "Reviewer 실패 — 현재 결과 유지: " + ex.Message);
                },
                isBuiltin: true,
                logPath: _logPath,
                modelOverride: "sonnet");   // 비교 태스크라 sonnet 이 충분히 빠름·정확

            if (_currentJobId == Guid.Empty)
                Complete(false, "Reviewer 큐 예약 실패");
        }

        private void HandleReviewerResult(string raw)
        {
            var json = TreeGenerator.ExtractJsonBlock(raw);
            AppendLog($"REVIEWER_RESPONSE (iter {_iteration})\n" + Truncate(json, 4000));

            ReviewerResult parsed;
            try { parsed = JsonUtility.FromJson<ReviewerResult>(json); }
            catch (Exception e)
            {
                Complete(true, "Reviewer 응답 파싱 실패 — 현재 결과 유지: " + e.Message);
                return;
            }

            if (parsed == null)
            {
                Complete(true, "Reviewer 응답이 비어있음 — 현재 결과 유지");
                return;
            }

            if (parsed.status == "ok")
            {
                // 유저 최종 수락 대기: 구독자 있으면 OnFinalReady invoke 후 pause
                if (OnFinalReady != null)
                {
                    _waitingForFinalAccept = true;
                    Status($"Reviewer ok (iter {_iteration}) — 유저 최종 수락 대기. Accept 또는 힌트 추가 후 Refine");
                    OnFinalReady.Invoke(new FinalState
                    {
                        prefabPath = _prefabPath,
                        screenshotPath = _screenshotPath,
                        treeSpecJson = _currentTreeSpec,
                        reviewerSummary = parsed.summary,
                        iterationsUsed = _iteration,
                    });
                    return;
                }

                // 구독자 없으면 자동 종료
                Complete(true, $"완료 (iter {_iteration}): " + (parsed.summary ?? ""));
                return;
            }

            // patches 추출 후 현재 TreeSpec 에 적용
            var patches = ExtractPatches(json);
            if (patches == null || patches.Count == 0)
            {
                Complete(true, "patches 없음 또는 파싱 실패 — 현재 결과 유지");
                return;
            }

            var patched = ApplyPatches(_currentTreeSpec, patches);
            if (string.IsNullOrEmpty(patched))
            {
                Complete(true, "patches 적용 실패 — 현재 결과 유지");
                return;
            }
            _currentTreeSpec = patched;
            AppendLog($"REVIEWER_NEEDS_FIX → {patches.Count} patch(es) applied, regenerating");
            GenerateAndScreenshot(initial: false);
        }

        [Serializable]
        private class ReviewerResult
        {
            public string status;
            public string summary;
            // updatedTreeSpec 은 JsonUtility 의 중첩 deserialization 이 제한적이라 raw 파싱으로 처리
        }

        /// <summary>
        /// Reviewer 응답 JSON 에서 `updatedTreeSpec` 필드의 value(중첩 JSON 객체)를 문자열로 추출
        /// </summary>
        /// <summary>
        /// Reviewer 응답에서 patches 배열 추출. 각 patch 는 path + 변경할 필드들 (preferredHeight 등) 로 구성.
        /// 응답 예: {""patches"": [{""path"":""MainFrame/Header"", ""preferredHeight"":280}, ...]}
        /// </summary>
        private static List<PatchEntry> ExtractPatches(string reviewerJson)
        {
            if (string.IsNullOrEmpty(reviewerJson)) return null;
            var key = "\"patches\"";
            var idx = reviewerJson.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = reviewerJson.IndexOf(':', idx);
            if (colonIdx < 0) return null;
            var arrStart = reviewerJson.IndexOf('[', colonIdx);
            if (arrStart < 0) return null;

            // 배열 범위 찾기 (문자열 안 [] 회피)
            int depth = 0;
            bool inStr = false, esc = false;
            int arrEnd = -1;
            for (int i = arrStart; i < reviewerJson.Length; i++)
            {
                char c = reviewerJson[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) { arrEnd = i; break; } }
            }
            if (arrEnd < 0) return null;

            var result = new List<PatchEntry>();
            // 배열 내부 각 object 파싱
            int pos = arrStart + 1;
            while (pos < arrEnd)
            {
                // 다음 '{' 찾기
                while (pos < arrEnd && reviewerJson[pos] != '{') pos++;
                if (pos >= arrEnd) break;
                int objStart = pos;
                int objDepth = 0;
                inStr = false; esc = false;
                int objEnd = -1;
                for (int i = objStart; i < arrEnd + 1; i++)
                {
                    char c = reviewerJson[i];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') { inStr = true; continue; }
                    if (c == '{') objDepth++;
                    else if (c == '}') { objDepth--; if (objDepth == 0) { objEnd = i; break; } }
                }
                if (objEnd < 0) break;

                var objJson = reviewerJson.Substring(objStart, objEnd - objStart + 1);
                var patch = ParsePatchObject(objJson);
                if (patch != null) result.Add(patch);
                pos = objEnd + 1;
            }
            return result;
        }

        private class PatchEntry
        {
            public string path;
            public Dictionary<string, object> fields = new Dictionary<string, object>();
        }

        private static PatchEntry ParsePatchObject(string objJson)
        {
            // JsonUtility 로 파싱하기 위해 임시 클래스에 맵핑하기보단 정규식/수동 파싱
            var e = new PatchEntry();
            // path 추출
            var pathMatch = System.Text.RegularExpressions.Regex.Match(
                objJson, "\"path\"\\s*:\\s*\"([^\"]*)\"");
            if (!pathMatch.Success) return null;
            e.path = pathMatch.Groups[1].Value;

            // 수치 필드 추출 (허용 필드만)
            string[] numFields = { "preferredHeight", "preferredWidth", "flexibleHeight", "flexibleWidth",
                                   "spacing", "fontSize", "offsetX", "offsetY" };
            foreach (var f in numFields)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    objJson, "\"" + f + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
                if (m.Success && float.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var v))
                {
                    e.fields[f] = v;
                }
            }

            // padding 배열 (4개 숫자)
            var padMatch = System.Text.RegularExpressions.Regex.Match(
                objJson, "\"padding\"\\s*:\\s*\\[\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)");
            if (padMatch.Success)
            {
                e.fields["padding"] = new[]
                {
                    float.Parse(padMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(padMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(padMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(padMatch.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            // size 배열 (루트용)
            var sizeMatch = System.Text.RegularExpressions.Regex.Match(
                objJson, "\"size\"\\s*:\\s*\\[\\s*(-?[0-9.]+)\\s*,\\s*(-?[0-9.]+)");
            if (sizeMatch.Success)
            {
                e.fields["size"] = new[]
                {
                    float.Parse(sizeMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(sizeMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            return e;
        }

        /// <summary>
        /// 현재 TreeSpec JSON 에 patches 를 적용해 새 JSON 반환.
        /// TreeGenerator.TreeSpec 으로 deserialize → path 로 노드 찾기 → 필드 덮어쓰기 → 재직렬화
        /// </summary>
        private static string ApplyPatches(string currentTreeSpecJson, List<PatchEntry> patches)
        {
            try
            {
                var spec = JsonUtility.FromJson<TreeGenerator.TreeSpec>(currentTreeSpecJson);
                if (spec == null || spec.root == null) return null;

                foreach (var p in patches)
                {
                    if (p == null) continue;

                    // path="" 는 루트 특수 처리 (size 등)
                    if (string.IsNullOrEmpty(p.path))
                    {
                        if (p.fields.TryGetValue("size", out var sz) && sz is float[] arr && arr.Length >= 2)
                            spec.size = arr;
                        ApplyFieldsToNode(spec.root, p.fields);
                        continue;
                    }

                    var node = FindNodeByPath(spec.root, p.path);
                    if (node == null)
                    {
                        UniMcpLogger.Warn("patch path 못 찾음: " + p.path);
                        continue;
                    }
                    ApplyFieldsToNode(node, p.fields);
                }

                return JsonUtility.ToJson(spec, prettyPrint: true);
            }
            catch (Exception e)
            {
                UniMcpLogger.Warn("ApplyPatches 실패: " + e.Message);
                return null;
            }
        }

        private static TreeGenerator.Node FindNodeByPath(TreeGenerator.Node root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            var current = root;
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (current.children == null) return null;
                TreeGenerator.Node next = null;
                foreach (var c in current.children)
                {
                    if (c != null && c.name == part) { next = c; break; }
                }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        private static void ApplyFieldsToNode(TreeGenerator.Node node, Dictionary<string, object> fields)
        {
            if (node == null || fields == null) return;
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "preferredHeight":  if (kv.Value is float ph) node.preferredHeight = ph; break;
                    case "preferredWidth":   if (kv.Value is float pw) node.preferredWidth = pw; break;
                    case "flexibleHeight":   if (kv.Value is float fh) node.flexibleHeight = fh; break;
                    case "flexibleWidth":    if (kv.Value is float fw) node.flexibleWidth = fw; break;
                    case "spacing":          if (kv.Value is float sp) node.spacing = sp; break;
                    case "fontSize":         if (kv.Value is float fs) node.fontSize = fs; break;
                    case "offsetX":          if (kv.Value is float ox) node.offsetX = ox; break;
                    case "offsetY":          if (kv.Value is float oy) node.offsetY = oy; break;
                    case "padding":          if (kv.Value is float[] p) node.padding = p; break;
                }
            }
        }

        private static string ExtractUpdatedTreeSpec(string reviewerJson)
        {
            if (string.IsNullOrEmpty(reviewerJson)) return null;
            var key = "\"updatedTreeSpec\"";
            var idx = reviewerJson.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;

            // 콜론 뒤 첫 '{' 찾기
            var colonIdx = reviewerJson.IndexOf(':', idx);
            if (colonIdx < 0) return null;
            var braceStart = reviewerJson.IndexOf('{', colonIdx);
            if (braceStart < 0) return null;

            // 중괄호 매칭 (문자열 내부 중괄호 회피 위해 간단한 파서)
            int depth = 0;
            bool inStr = false;
            bool esc = false;
            for (int i = braceStart; i < reviewerJson.Length; i++)
            {
                char c = reviewerJson[i];
                if (inStr)
                {
                    if (esc) { esc = false; }
                    else if (c == '\\') { esc = true; }
                    else if (c == '"') { inStr = false; }
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return reviewerJson.Substring(braceStart, i - braceStart + 1);
                }
            }
            return null;
        }

        private void SaveTreeSpecToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                _treeSpecJsonPath = Path.Combine(dir, $"{_jobIdShort}-treespec-iter{_iteration + 1}.json")
                    .Replace('\\', '/');
                File.WriteAllText(_treeSpecJsonPath, _currentTreeSpec);
            }
            catch (Exception e)
            {
                AppendLog("TreeSpec 파일 저장 실패: " + e.Message);
            }
        }

        private void AppendCreatedAssetToManifest(string prefabPath)
        {
            if (string.IsNullOrEmpty(_manifestPath) || !File.Exists(_manifestPath)) return;
            try
            {
                var m = JsonUtility.FromJson<ManifestShape>(File.ReadAllText(_manifestPath));
                if (m == null) return;
                var list = new List<string>(m.createdAssets ?? Array.Empty<string>());
                if (!list.Contains(prefabPath)) list.Add(prefabPath);
                m.createdAssets = list.ToArray();
                File.WriteAllText(_manifestPath, JsonUtility.ToJson(m, prettyPrint: true));
            }
            catch (Exception e) { UniMcpLogger.Warn("manifest append 실패: " + e.Message); }
        }

        [Serializable]
        private class ManifestShape
        {
            public string jobId;
            public string imagePath;
            public string outputFolder;
            public string[] createdAssets;
        }

        private void Complete(bool success, string msg)
        {
            try { AssetDatabase.Refresh(); } catch { }
            AppendLog($"PIPELINE_END  success={success} iter={_iteration} msg={msg}");
            _onComplete?.Invoke(new Result
            {
                success = success,
                prefabPath = _prefabPath,
                iterationsUsed = _iteration,
                error = success ? null : msg,
                summary = msg,
            });
        }

        private void Status(string s) => _onStatus?.Invoke(s);

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); }
            catch { }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "\n… (truncated)";
        }
    }
}
