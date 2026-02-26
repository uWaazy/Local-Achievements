using System;

namespace LocalAchievements.Services
{
    public static class AdvancedLogger
    {
        public static void Log(string message) { }
        public static void LogBinaryDump(byte[] fileBytes) { }
        public static void LogMatchAttempt(string apiName, byte[] apiNameBytes, bool matchFound) { }
    }
}