using System;
using System.IO;
using System.Text;

namespace UniMCP.Editor.Logging
{
    /// <summary>
    /// UniMCP 파일 로거.
    /// 작업 실행 중(ActiveLogPath 세팅됨)이면 해당 작업 파일에, 그 외엔 `~/Documents/UniMCP/Debug/Logs/{yyyy-MM-dd}.log` 에 append.
    /// Unity Console 에는 출력하지 않는다
    /// </summary>
    public static class UniMcpLogger
    {
        private static readonly object _lock = new();

        /// <summary>
        /// 현재 작업 단위 로그 파일 경로. RunQueue 가 작업 시작/종료 시 세팅한다.
        /// 세팅되면 모든 로그 엔트리가 이 파일로 라우팅되어 일일 로그는 더 이상 커지지 않는다
        /// </summary>
        public static string ActiveLogPath { get; set; }

        private static string FallbackLogDir
        {
            get
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(docs, "UniMCP", "Debug", "Logs");
            }
        }

        private static string FallbackLogPath
        {
            get
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                return Path.Combine(FallbackLogDir, $"{today}.log");
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
                var active = ActiveLogPath;
                var path = !string.IsNullOrEmpty(active) ? active : FallbackLogPath;
                var dir = Path.GetDirectoryName(path);

                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
                    var line = $"[{ts}] [{level}] {message ?? ""}{Environment.NewLine}";
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch { /* 로깅 실패는 삼킨다 */ }
        }
    }
}
