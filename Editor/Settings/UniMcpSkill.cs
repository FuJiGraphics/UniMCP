using System;

namespace UniMCP.Editor.Settings
{
    [Serializable]
    public class UniMcpSkill
    {
        public string name;
        public string prompt;

        public UniMcpSkill Clone() => new() { name = name, prompt = prompt };
    }
}
