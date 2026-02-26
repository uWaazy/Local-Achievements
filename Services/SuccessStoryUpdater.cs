using LocalAchievements.Models;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LocalAchievements.Services
{
    /// <summary>
    /// Gerencia a atualização dos dados de conquistas do plugin SuccessStory.
    /// </summary>
    public class SuccessStoryUpdater
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI _api;
        private const string SuccessStoryGuid = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private const string LogPath = @"C:\Users\Public\Desktop\Playnite_SuccessStory_Log.txt";

        // Modelo simplificado para deserializar o JSON do SuccessStory
        private class SuccessStoryAchievement
        {
            public string Name { get; set; } 
            public string ApiName { get; set; } // Adicionado para suportar o ID real
            public DateTime? DateUnlocked { get; set; }
            public bool IsHidden { get; set; }
        }

        public SuccessStoryUpdater(IPlayniteAPI api)
        {
            _api = api;
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* Ignora falhas de log */ }
        }

        /// <summary>
        /// Atualiza o arquivo de dados do SuccessStory com as conquistas desbloqueadas.
        /// </summary>
        /// <returns>Quantidade de conquistas sincronizadas.</returns>
        public int UpdateAchievements(Game game, Dictionary<string, DateTime> unlockedAchievements)
        {
            Log("--------------------------------------------------");
            Log($"Iniciando atualização para: {game.Name} (ID: {game.GameId})");
            Log($"Conquistas recebidas do VdfReader: {unlockedAchievements.Count}");

            string successStoryDataPath = Path.Combine(_api.Paths.ExtensionsDataPath, SuccessStoryGuid, "SuccessStory");
            if (!Directory.Exists(successStoryDataPath))
            {
                Log("ERRO: Pasta de dados do SuccessStory não encontrada.");
                return 0;
            }

            // O SuccessStory nomeia os arquivos como [Source]_[GameId].json (ex: Steam_1196590.json)
            string gameId = game.GameId;
            string sourceName = "Playnite";

            if (game.Source != null && !string.IsNullOrEmpty(game.Source.Name))
            {
                sourceName = game.Source.Name;
            }
            else if (game.PluginId == Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
            {
                sourceName = "Steam";
            }

            string achievementFilePath = Path.Combine(successStoryDataPath, $"{sourceName}_{gameId}.json");

            Log($"Tentando ler arquivo JSON em: {achievementFilePath}");

            if (!File.Exists(achievementFilePath))
            {
                Log("ERRO: Arquivo JSON não encontrado. Certifique-se de que o SuccessStory baixou os dados do jogo.");
                return 0;
            }

            try
            {
                string json = File.ReadAllText(achievementFilePath);
                var achievements = JsonConvert.DeserializeObject<List<SuccessStoryAchievement>>(json);
                
                if (achievements == null)
                {
                    Log("ERRO: JSON vazio ou inválido.");
                    return 0;
                }

                int updatedCount = 0;

                // Itera sobre as conquistas do SuccessStory e atualiza a data de desbloqueio
                foreach (var ach in achievements)
                {
                    // Tenta usar o ApiName (ID técnico) primeiro, se não existir, usa o Name (Display Name)
                    string targetName = !string.IsNullOrEmpty(ach.ApiName) ? ach.ApiName : ach.Name;
                    
                    if (string.IsNullOrEmpty(targetName)) continue;

                    // Procura uma chave no dicionário local que contenha o nome do JSON (tolerância a sufixos)
                    // Ex: JSON="ACH_01", Local="ACH_01_NAME" -> Match
                    var matchKey = unlockedAchievements.Keys.FirstOrDefault(k => 
                        string.Equals(k, targetName, StringComparison.OrdinalIgnoreCase) ||
                        k.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (matchKey != null)
                    {
                        // Se encontrou e ainda não está desbloqueada
                        if (ach.DateUnlocked == null)
                        {
                            ach.DateUnlocked = unlockedAchievements[matchKey];
                            updatedCount++;
                            Log($"Match encontrado: JSON='{targetName}' | RAW='{matchKey}' -> DESBLOQUEADO!");
                        }
                    }
                }

                if (updatedCount > 0)
                {
                    string updatedJson = JsonConvert.SerializeObject(achievements, Formatting.Indented);
                    File.WriteAllText(achievementFilePath, updatedJson);
                    Log($"SUCESSO: Arquivo salvo. Total atualizados: {updatedCount}");
                }
                else
                {
                    Log("Nenhuma nova conquista correspondente encontrada para atualizar.");
                }

                return updatedCount;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                logger.Error(ex, $"Falha ao atualizar o arquivo do SuccessStory para '{game.Name}'.");
                return 0;
            }
        }
    }
}