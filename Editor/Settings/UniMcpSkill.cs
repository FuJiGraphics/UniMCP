using System;
using System.Collections.Generic;
using System.Linq;

namespace UniMCP.Editor.Settings
{
    [Serializable]
    public class UniMcpSkill
    {
        public string name;
        public string prompt;
        public List<UniMcpSkillFile> files = new();
        public List<string> folders = new();

        public UniMcpSkill Clone()
        {
            return new UniMcpSkill
            {
                name = name,
                prompt = prompt,
                files = files == null
                    ? new List<UniMcpSkillFile>()
                    : files.Select(f => f.Clone()).ToList(),
                folders = folders == null
                    ? new List<string>()
                    : folders.ToList(),
            };
        }
    }
}
