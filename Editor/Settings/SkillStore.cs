using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UniMCP.Editor.Logging;
using UnityEngine;

namespace UniMCP.Editor.Settings
{
    /// <summary>
    /// UniMcpSkill 리스트를 `.claude/skills/<name>/` 폴더 트리로 동기화한다.
    /// SKILL.md에 `unimcp_managed: true` 마커가 있는 경우에만 덮어쓰기·삭제하므로 외부에서 만든 스킬은 건드리지 않는다
    /// </summary>
    public static class SkillStore
    {
        public const string ManagedPrefix = "unimcp-";
        private const string ManagedMarker = "unimcp_managed: true";

        private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        private static string SkillsRoot => Path.Combine(ProjectRoot, ".claude", "skills");

        public static string GetInvocationName(string skillName)
        {
            var safe = SanitizeName(skillName);
            return string.IsNullOrEmpty(safe) ? "" : ManagedPrefix + safe;
        }

        public static string GetSkillDirectoryPath(string skillName)
        {
            var dir = GetDirName(skillName);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(SkillsRoot, dir);
        }

        /// <summary>
        /// 디스크에 저장된 모든 managed 스킬 디렉토리를 스캔해 이름 리스트를 반환한다
        /// </summary>
        public static List<string> DiscoverNamesOnDisk()
        {
            var names = new List<string>();

            if (!Directory.Exists(SkillsRoot))
                return names;

            foreach (var dir in Directory.GetDirectories(SkillsRoot))
            {
                var dirName = Path.GetFileName(dir);

                if (!dirName.StartsWith(ManagedPrefix))
                    continue;

                var skillMd = Path.Combine(dir, "SKILL.md");

                if (!File.Exists(skillMd))
                    continue;

                string content;
                try { content = File.ReadAllText(skillMd); }
                catch { continue; }

                if (!content.Contains(ManagedMarker))
                    continue;

                // 디렉토리 이름에서 접두사 제거 → 스킬 이름 추정
                var nameGuess = dirName.Substring(ManagedPrefix.Length).Replace('-', ' ');
                names.Add(nameGuess);
            }

            return names;
        }

        /// <summary>
        /// 디스크에서 스킬 한 개를 읽어 UniMcpSkill 객체로 반환. 없으면 null
        /// </summary>
        public static UniMcpSkill LoadFromDisk(string skillName)
        {
            var dir = GetSkillDirectoryPath(skillName);

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var skillMdPath = Path.Combine(dir, "SKILL.md");

            if (!File.Exists(skillMdPath))
                return null;

            string content;
            try { content = File.ReadAllText(skillMdPath); }
            catch { return null; }

            if (!content.Contains(ManagedMarker))
                return null;

            return new UniMcpSkill
            {
                name = skillName,
                prompt = StripFrontmatter(content),
                files = ReadAllSubFiles(dir),
                folders = ReadAllFolders(dir),
            };
        }

        private static string StripFrontmatter(string content)
        {
            var match = Regex.Match(content, @"^---\s*\r?\n[\s\S]*?\r?\n---\s*\r?\n\r?\n?");
            return match.Success ? content.Substring(match.Length) : content;
        }

        private static List<UniMcpSkillFile> ReadAllSubFiles(string skillDir)
        {
            var result = new List<UniMcpSkillFile>();

            foreach (var fullPath in Directory.GetFiles(skillDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(skillDir, fullPath).Replace('\\', '/');

                if (relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    result.Add(new UniMcpSkillFile
                    {
                        path = relative,
                        content = File.ReadAllText(fullPath),
                    });
                }
                catch { }
            }

            return result;
        }

        private static List<string> ReadAllFolders(string skillDir)
        {
            var result = new List<string>();

            foreach (var dir in Directory.GetDirectories(skillDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(skillDir, dir).Replace('\\', '/');
                result.Add(relative);
            }

            return result;
        }

        public static void Sync(IEnumerable<UniMcpSkill> previous, IEnumerable<UniMcpSkill> current)
        {
            Directory.CreateDirectory(SkillsRoot);

            var previousList = previous?.ToList() ?? new List<UniMcpSkill>();
            var currentList = current?.ToList() ?? new List<UniMcpSkill>();

            foreach (var skill in currentList)
                MigrateFromUnprefixed(skill);

            var currentDirs = new HashSet<string>(
                currentList.Select(s => GetDirName(s.name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var prev in previousList)
            {
                var dir = GetDirName(prev.name);
                if (!currentDirs.Contains(dir))
                    TryDeleteManagedSkillDir(dir);
            }

            foreach (var skill in currentList)
            {
                var prev = previousList.FirstOrDefault(
                    p => GetDirName(p.name) == GetDirName(skill.name));
                WriteSkill(skill, prev);
            }
        }

        private static string GetDirName(string skillName)
        {
            var safe = SanitizeName(skillName);
            return string.IsNullOrEmpty(safe) ? "" : ManagedPrefix + safe;
        }

        private static void MigrateFromUnprefixed(UniMcpSkill skill)
        {
            var safe = SanitizeName(skill.name);
            if (string.IsNullOrEmpty(safe))
                return;

            var oldDir = Path.Combine(SkillsRoot, safe);
            var newDir = Path.Combine(SkillsRoot, ManagedPrefix + safe);
            if (!Directory.Exists(oldDir) || string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                return;

            var oldSkillMd = Path.Combine(oldDir, "SKILL.md");
            if (!File.Exists(oldSkillMd))
                return;

            string content;
            try { content = File.ReadAllText(oldSkillMd); }
            catch { return; }

            if (!content.Contains(ManagedMarker))
                return;

            try { Directory.Delete(oldDir, recursive: true); }
            catch (Exception e)
            {
                UniMcpLogger.Warn($"Failed to migrate old skill dir '{safe}': {e.Message}");
            }
        }

        private static void WriteSkill(UniMcpSkill skill, UniMcpSkill previous)
        {
            var dirName = GetDirName(skill.name);
            if (string.IsNullOrEmpty(dirName))
                return;

            var dir = Path.Combine(SkillsRoot, dirName);
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
                    catch (Exception e) { UniMcpLogger.Warn($"Failed to delete skill file '{prevFile.path}': {e.Message}"); }
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
                        UniMcpLogger.Warn($"Failed to prune folder '{prevFolder}': {e.Message}");
                    }
                }
            }
        }

        private static void TryDeleteManagedSkillDir(string dirName)
        {
            if (string.IsNullOrEmpty(dirName))
                return;

            var dir = Path.Combine(SkillsRoot, dirName);
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                return;

            string content;
            try { content = File.ReadAllText(file); }
            catch { return; }

            if (!content.Contains(ManagedMarker))
                return;

            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) { UniMcpLogger.Warn($"Failed to delete skill '{dirName}': {e.Message}"); }
        }

        private static string BuildSkillFile(UniMcpSkill skill)
        {
            var invocation = GetInvocationName(skill.name);
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {invocation}");
            sb.AppendLine($"description: UniMCP-managed skill ({skill.name}).");
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
