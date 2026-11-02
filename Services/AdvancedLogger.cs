using Playnite.SDK;
using System;
using System.IO;

namespace LocalAchievements.Services
{
    public static class AdvancedLogger
    {
        private static readonly ILogger playniteLogger = LogManager.GetLogger();
        private static readonly object fileLock = new object();
        private const long MaxLogBytes = 1024 * 1024;
        private static string logPath;

        public static string LogFilePath => logPath;

        public static void Initialize(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                logPath = Path.Combine(directory, "LocalAchievements.log");
            }
            catch { }
        }

        public static void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            try { playniteLogger.Info(message); } catch { }

            if (logPath == null) return;

            lock (fileLock)
            {
                try
                {
                    var info = new FileInfo(logPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string old = logPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(logPath, old);
                    }
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
                catch { }
            }
        }

        public static void LogBinaryDump(byte[] fileBytes) { }
        public static void LogMatchAttempt(string apiName, byte[] apiNameBytes, bool matchFound) { }
    }
}
