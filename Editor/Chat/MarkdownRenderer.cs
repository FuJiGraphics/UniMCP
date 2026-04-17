using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UniMCP.Editor.Chat
{
    /// <summary>
    /// 마크다운을 Unity IMGUI rich text 태그 문자열로 변환한다.
    /// 코드 블록·인라인 코드·헤더·볼드·이탤릭·링크·리스트를 처리한다
    /// </summary>
    public static class MarkdownRenderer
    {
        private static readonly Regex CodeBlock = new(
            @"```(?:[a-zA-Z0-9_+-]*)\r?\n(.*?)\r?\n```",
            RegexOptions.Singleline);

        private static readonly Regex InlineCode = new(@"`([^`\n]+?)`");

        private static readonly Regex Header = new(
            @"^(#{1,6})[ \t]+(.+?)\s*$",
            RegexOptions.Multiline);

        private static readonly Regex Bold = new(@"\*\*(.+?)\*\*");

        private static readonly Regex Italic = new(
            @"(?<![\*\w])\*(?!\*)([^\*\n]+?)\*(?![\*\w])");

        private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)");

        private const string ColorCode   = "#c9a8ff";
        private const string ColorLink   = "#6ba3ff";
        private const string ColorHeader = "#ffd48a";

        public static string ToRichText(string md)
        {
            if (string.IsNullOrEmpty(md))
                return md;

            var codeBlocks = new List<string>();
            md = CodeBlock.Replace(md, m =>
            {
                codeBlocks.Add(m.Groups[1].Value);
                return $"\0CB{codeBlocks.Count - 1}\0";
            });

            var inlineCodes = new List<string>();
            md = InlineCode.Replace(md, m =>
            {
                inlineCodes.Add(m.Groups[1].Value);
                return $"\0IC{inlineCodes.Count - 1}\0";
            });

            md = Header.Replace(md, m =>
            {
                var level = m.Groups[1].Value.Length;
                var size = level switch { 1 => 16, 2 => 14, 3 => 13, _ => 12 };
                return $"<size={size}><b><color={ColorHeader}>{m.Groups[2].Value}</color></b></size>";
            });

            md = Bold.Replace(md, "<b>$1</b>");
            md = Italic.Replace(md, "<i>$1</i>");
            md = Link.Replace(md, $"<color={ColorLink}>$1</color> <i>($2)</i>");

            for (int i = 0; i < inlineCodes.Count; i++)
                md = md.Replace($"\0IC{i}\0", $"<color={ColorCode}>{Escape(inlineCodes[i])}</color>");

            for (int i = 0; i < codeBlocks.Count; i++)
                md = md.Replace(
                    $"\0CB{i}\0",
                    $"\n<color={ColorCode}>{Escape(codeBlocks[i])}</color>\n");

            return md;
        }

        private static string Escape(string s)
        {
            return s.Replace("<", "\u200B<").Replace(">", ">\u200B");
        }
    }
}
