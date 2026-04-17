using System;
using System.IO;
using System.Text;

namespace UniMCP.Editor.Logging
{
    /// <summary>
    /// UniMCP 파일 로거.
    /// `~/Documents/UniMCP/Debug/Logs/{yyyy-MM-dd}.log` 에 append.
    /// Unity Console 에는 출력하지 않는다
    /// </summary>
    public static class UniMcpLogger
    {
        private static readonly object _lock = new();

        private static string LogDir
        {
            get
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(docs, "UniMCP", "Debug", "Logs");
            }
        }

        private static string LogPath
        {
            get
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                return Path.Combine(LogDir, $"{today}.log");
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Exception(Exception e)
        {
            Write("ERROR", (e?.Message ?? "") + "\n" + (e?.StackTrace ?? ""));
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDir);
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    var line = $"[{ts}] [{level}] {message ?? ""}{Environment.NewLine}";
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch { /* 로깅 실패는 삼킨다 */ }
        }
    }
}
