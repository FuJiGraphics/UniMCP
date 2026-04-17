using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMcpSkill 리스트를 `.claude/skills/<name>/SKILL.md` 파일로 동기화한다.
    /// 프론트매터에 `unimcp_managed: true` 마커가 있는 스킬만 삭제·덮어쓰므로 외부에서 만든 스킬은 건드리지 않는다
    /// </summary>
    public static class SkillStore
    {
        private const string ManagedMarker = "unimcp_managed: true";

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        private static string SkillsRoot => Path.Combine(ProjectRoot, ".claude", "skills");

        public static void Sync(IEnumerable<UniMcpSkill> previous, IEnumerable<UniMcpSkill> current)
        {
            Directory.CreateDirectory(SkillsRoot);

            var currentNames = new HashSet<string>(current.Select(s => s.name ?? ""));

            foreach (var removed in previous.Select(s => s.name).Where(n => !currentNames.Contains(n)))
                TryDeleteManagedSkill(removed);

            foreach (var skill in current)
                WriteSkill(skill);
        }

        private static void WriteSkill(UniMcpSkill skill)
        {
            var safe = SanitizeName(skill.name);
            if (string.IsNullOrEmpty(safe))
                return;

            var dir = Path.Combine(SkillsRoot, safe);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "SKILL.md"), BuildSkillFile(skill));
        }

        private static void TryDeleteManagedSkill(string name)
        {
            var safe = SanitizeName(name);
            if (string.IsNullOrEmpty(safe))
                return;

            var dir = Path.Combine(SkillsRoot, safe);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                return;

            string content;
            try { content = File.ReadAllText(file); }
            catch { return; }

            if (!content.Contains(ManagedMarker))
                return;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UniMCP] Failed to delete skill '{name}': {e.Message}");
            }
        }

        private static string BuildSkillFile(UniMcpSkill skill)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {skill.name}");
            sb.AppendLine("description: Project-specific skill managed by UniMCP.");
            sb.AppendLine(ManagedMarker);
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(skill.prompt ?? "");
            return sb.ToString();
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new StringBuilder();
            foreach (var c in name)
            {
                if (invalid.Contains(c) || c == ' ')
                    cleaned.Append('-');
                else
                    cleaned.Append(c);
            }
            return cleaned.ToString().Trim('-');
        }
    }
}
