using System;

namespace UniMCP.Editor.Settings
{
    [Serializable]
    public class UniMcpSkillFile
    {
        public string path;
        public string content;

        public UniMcpSkillFile Clone() => new() { path = path, content = content };
    }
}
