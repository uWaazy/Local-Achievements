using System;

namespace LocalAchievements.Services
{
    public static class AdvancedLogger
    {
        // Métodos vazios para manter a compatibilidade com o resto do código, 
        // mas parando completamente de gerar o arquivo na Área de Trabalho.
        public static void Log(string message) { }
        public static void LogBinaryDump(byte[] fileBytes) { }
        public static void LogMatchAttempt(string apiName, byte[] apiNameBytes, bool matchFound) { }
    }
}