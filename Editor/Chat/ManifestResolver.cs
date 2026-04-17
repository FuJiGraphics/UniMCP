using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UniMCP.Editor.Chat
{
    /// <summary>
    /// Unity 프로젝트의 `Packages/manifest.json`에서 `file:` 의존성을 파싱해 프로젝트 외부 경로를 반환한다.
    /// 챗 스코프 확장을 위해 `--add-dir` 인자로 사용된다
    /// </summary>
    public static class ManifestResolver
    {
        private static readonly Regex FileDep = new(
            "\"[\\w.\\-]+\"\\s*:\\s*\"file:([^\"]+)\"",
            RegexOptions.IgnoreCase);

        public static List<string> GetExternalPackageDirs(string projectRoot)
        {
            var result = new List<string>();
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
                return result;

            string json;
            try { json = File.ReadAllText(manifestPath); }
            catch { return result; }

            var packagesDir = Path.Combine(projectRoot, "Packages");
            var projectFull = NormalizeDir(Path.GetFullPath(projectRoot));

            foreach (Match m in FileDep.Matches(json))
            {
                var rel = m.Groups[1].Value;
                string abs;
                try
                {
                    abs = Path.IsPathRooted(rel)
                        ? Path.GetFullPath(rel)
                        : Path.GetFullPath(Path.Combine(packagesDir, rel));
                }
                catch { continue; }

                var absNorm = NormalizeDir(abs);

                if (absNorm.StartsWith(projectFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Directory.Exists(abs))
                    continue;

                if (!result.Contains(abs))
                    result.Add(abs);
            }

            return result;
        }

        private static string NormalizeDir(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed + Path.DirectorySeparatorChar;
        }
    }
}
