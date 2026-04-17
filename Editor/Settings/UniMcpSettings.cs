using System;
using System.Collections.Generic;
using System.Linq;
using UniMCP.Editor.Logging;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMCP 프로젝트 설정. `ProjectSettings/UniMcpSettings.asset`에 저장되어 팀 간 공유된다.
    /// 스킬 실제 내용은 `.claude/skills/unimcp-*/` 디스크에 저장되고, 여기선 이름 리스트만 관리한다
    /// </summary>
    [FilePath("ProjectSettings/UniMcpSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class UniMcpSettings : ScriptableSingleton<UniMcpSettings>
    {
        [SerializeField] private List<string> _skillNames = new();
        [SerializeField] private int _maxConcurrentJobs = 1;

        // Legacy: 이전 버전에서 asset 안에 prompt/files 내용을 박아두던 필드.
        // 마이그레이션용으로만 남기고, 새 저장은 _skillNames 로 이관한다
        [SerializeField] private List<UniMcpSkill> _skills = new();

        private bool _migrated;

        /// <summary>
        /// 스킬 동시 실행 최대 개수. 1이면 직렬 처리.
        /// 동일 타겟 파일은 개수 상관없이 항상 직렬로 처리된다
        /// </summary>
        public int MaxConcurrentJobs
        {
            get => Mathf.Max(1, _maxConcurrentJobs);
            set
            {
                var clamped = Mathf.Clamp(value, 1, 16);

                if (_maxConcurrentJobs == clamped)
                    return;

                _maxConcurrentJobs = clamped;
                Save(true);
            }
        }

        public IReadOnlyList<UniMcpSkill> Skills
        {
            get
            {
                EnsureInitialized();

                var result = new List<UniMcpSkill>();

                foreach (var name in _skillNames)
                {
                    var skill = SkillStore.LoadFromDisk(name);

                    if (skill != null)
                        result.Add(skill);
                }

                return result;
            }
        }

        public static event Action SkillsChanged;

        /// <summary>
        /// 스킬 전체를 디스크에 쓰고, asset에는 이름만 저장한다
        /// </summary>
        public void SetSkills(IEnumerable<UniMcpSkill> skills)
        {
            EnsureInitialized();

            var list = skills.Select(s => s.Clone()).ToList();
            var previous = Skills.ToList();

            SkillStore.Sync(previous, list);

            _skillNames = list.Select(s => s.name).ToList();
            _skills.Clear();
            Save(true);

            SkillsChanged?.Invoke();
        }

        public void ForceSave() => Save(true);

        /// <summary>
        /// 1회성 마이그레이션.
        /// legacy `_skills` 필드에 prompt 내용이 박혀있으면 디스크에 쓰고 `_skillNames` 로 이관.
        /// `_skillNames` 가 비어있고 디스크엔 스킬 폴더가 있으면 스캔해서 이름 복원
        /// </summary>
        private void EnsureInitialized()
        {
            if (_migrated)
                return;

            _migrated = true;

            var legacyHasContent = _skills != null
                && _skills.Any(s => !string.IsNullOrEmpty(s?.prompt));

            if (legacyHasContent)
            {
                // 디스크에 이미 관리형 스킬 폴더가 있으면 그게 최신 — 덮어쓰지 않고 이름만 이관
                var onDisk = SkillStore.DiscoverNamesOnDisk();

                if (onDisk.Count == 0)
                    SkillStore.Sync(new List<UniMcpSkill>(), _skills.ToList());

                _skillNames = _skills
                    .Where(s => !string.IsNullOrWhiteSpace(s?.name))
                    .Select(s => s.name)
                    .Distinct()
                    .ToList();
                _skills.Clear();
                Save(true);

                var note = onDisk.Count == 0 ? "디스크로 이관" : "디스크가 최신이라 이름만 이관";
                UniMcpLogger.Info($"legacy skills({_skillNames.Count}) {note} 완료.");
                return;
            }

            if (_skillNames.Count == 0)
            {
                var discovered = SkillStore.DiscoverNamesOnDisk();

                if (discovered.Count > 0)
                {
                    _skillNames = discovered;
                    Save(true);
                    UniMcpLogger.Info($"디스크에서 스킬 {discovered.Count}개 발견 → asset 에 등록");
                }
            }
        }
    }
}
