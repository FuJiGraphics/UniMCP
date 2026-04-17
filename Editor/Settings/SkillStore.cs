using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMcpSkill 리스트를 `.claude/skills/<name>/` 폴더 트리로 동기화한다.
    /// SKILL.md에 `unimcp_managed: true` 마커가 있는 경우에만 덮어쓰기·삭제하므로 외부에서 만든 스킬은 건드리지 않는다
    /// </summary>
    public static class SkillStore
    {
        private const string ManagedMarker = "unimcp_managed: true";

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        private static string SkillsRoot => Path.Combine(ProjectRoot, ".claude", "skills");

        public static void Sync(IEnumerable<UniMcpSkill> previous, IEnumerable<UniMcpSkill> current)
        {
            Directory.CreateDirectory(SkillsRoot);

            var previousList = previous?.ToList() ?? new List<UniMcpSkill>();
            var currentList = current?.ToList() ?? new List<UniMcpSkill>();

            var currentNames = new HashSet<string>(currentList.Select(s => SanitizeName(s.name)));

            foreach (var prev in previousList)
            {
                var safe = SanitizeName(prev.name);
                if (!currentNames.Contains(safe))
                    TryDeleteManagedSkillDir(safe);
            }

            foreach (var skill in currentList)
            {
                var prev = previousList.FirstOrDefault(p => SanitizeName(p.name) == SanitizeName(skill.name));
                WriteSkill(skill, prev);
            }
        }

        private static void WriteSkill(UniMcpSkill skill, UniMcpSkill previous)
        {
            var safe = SanitizeName(skill.name);
            if (string.IsNullOrEmpty(safe))
                return;

            var dir = Path.Combine(SkillsRoot, safe);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "SKILL.md"), BuildSkillFile(skill));

            foreach (var folderPath in skill.folders ?? new List<string>())
            {
                var fullFolder = Path.Combine(dir, folderPath);
                Directory.CreateDirectory(fullFolder);
            }

            foreach (var file in skill.files ?? new List<UniMcpSkillFile>())
            {
                if (string.IsNullOrEmpty(file?.path))
                    continue;
                if (file.path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullPath = Path.Combine(dir, file.path);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllText(fullPath, file.content ?? "");
            }

            if (previous != null)
            {
                var currentPaths = new HashSet<string>(
                    (skill.files ?? new List<UniMcpSkillFile>())
                        .Where(f => !string.IsNullOrEmpty(f?.path))
                        .Select(f => f.path.Replace('\\', '/')),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var prevFile in previous.files ?? new List<UniMcpSkillFile>())
                {
                    if (string.IsNullOrEmpty(prevFile?.path))
                        continue;
                    var norm = prevFile.path.Replace('\\', '/');
                    if (currentPaths.Contains(norm))
                        continue;

                    var toDelete = Path.Combine(dir, prevFile.path);
                    try { if (File.Exists(toDelete)) File.Delete(toDelete); }
                    catch (Exception e) { Debug.LogWarning($"[UniMCP] Failed to delete skill file '{prevFile.path}': {e.Message}"); }
                }

                var currentFolders = new HashSet<string>(
                    skill.folders ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var prevFolder in previous.folders ?? new List<string>())
                {
                    if (currentFolders.Contains(prevFolder))
                        continue;

                    var fullFolder = Path.Combine(dir, prevFolder);
                    try
                    {
                        if (Directory.Exists(fullFolder)
                            && !Directory.EnumerateFileSystemEntries(fullFolder).Any())
                        {
                            Directory.Delete(fullFolder);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[UniMCP] Failed to prune folder '{prevFolder}': {e.Message}");
                    }
                }
            }
        }

        private static void TryDeleteManagedSkillDir(string safeName)
        {
            if (string.IsNullOrEmpty(safeName))
                return;

            var dir = Path.Combine(SkillsRoot, safeName);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                return;

            string content;
            try { content = File.ReadAllText(file); }
            catch { return; }

            if (!content.Contains(ManagedMarker))
                return;

            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) { Debug.LogWarning($"[UniMCP] Failed to delete skill '{safeName}': {e.Message}"); }
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
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ')
                    cleaned.Append('-');
                else
                    cleaned.Append(c);
            }
            return cleaned.ToString().Trim('-');
        }
    }
}
