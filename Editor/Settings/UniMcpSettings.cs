using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMCP 프로젝트 설정. `ProjectSettings/UniMcpSettings.asset`에 저장되어 팀 간 공유된다.
    /// 프리팹 컨벤션과 프로젝트 전용 스킬 목록을 보관한다
    /// </summary>
    [FilePath("ProjectSettings/UniMcpSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class UniMcpSettings : ScriptableSingleton<UniMcpSettings>
    {
        [SerializeField, TextArea(10, 40)] private string _prefabConvention = "";
        [SerializeField] private List<UniMcpSkill> _skills = new();

        public string PrefabConvention
        {
            get => _prefabConvention;
            set
            {
                if (_prefabConvention == value)
                    return;
                _prefabConvention = value;
                Save(true);
            }
        }

        public bool IsPrefabConventionDefined => !string.IsNullOrWhiteSpace(_prefabConvention);

        public IReadOnlyList<UniMcpSkill> Skills => _skills;

        public void SetSkills(IEnumerable<UniMcpSkill> skills)
        {
            _skills = skills.Select(s => s.Clone()).ToList();
            Save(true);
        }

        public void ForceSave() => Save(true);
    }
}
