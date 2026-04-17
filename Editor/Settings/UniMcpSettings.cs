using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMCP 프로젝트 설정. `ProjectSettings/UniMcpSettings.asset`에 저장되어 팀 간 공유된다.
    /// 프리팹 컨벤션 등 UniMCP 기능이 참조하는 규칙을 보관한다
    /// </summary>
    [FilePath("ProjectSettings/UniMcpSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class UniMcpSettings : ScriptableSingleton<UniMcpSettings>
    {
        [SerializeField, TextArea(10, 40)] private string _prefabConvention = "";

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

        public void ForceSave() => Save(true);
    }
}
