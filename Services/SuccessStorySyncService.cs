using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LocalAchievements.Services
{
    public class SuccessStoryDatabase { 
        public List<SuccessStoryAchievement> Items { get; set; } 
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }
    public class SuccessStoryAchievement {
        public string Name { get; set; }
        public string ApiName { get; set; }
        public DateTime? DateUnlocked { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public class SuccessStorySyncService
    {
        private readonly IPlayniteAPI _api;
        private const string SSGuid = "cebe6d32-8c46-4459-b993-5a5189d60788";

        public SuccessStorySyncService(IPlayniteAPI api) { _api = api; }

        public bool IsSteamToolsGame(Game game)
        {
            try
            {
                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString().Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath) || string.IsNullOrEmpty(game.GameId)) return false;

                string luaPath = Path.Combine(steamPath, "config", "stplug-in", $"{game.GameId}.lua");
                return File.Exists(luaPath);
            }
            catch { return false; }
        }

        public int Sync(Game game)
        {
            try
            {
                AdvancedLogger.Log($"=== Iniciando Injeção Cirúrgica: {game.Name} ===");

                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString().Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath)) return 0;

                string binDir = Path.Combine(steamPath, "appcache", "stats");
                if (!Directory.Exists(binDir)) return 0;

                var files = Directory.GetFiles(binDir, $"UserGameStats_*_{game.GameId}.bin");
                if (files.Length == 0) return 0;

                string binPath = files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                string schemaPath = Path.Combine(binDir, $"UserGameStatsSchema_{game.GameId}.bin");
                
                var schemaMap = VdfReader.LoadSchemaTranslation(schemaPath);
                var localAch = VdfReader.ExtractAchievements(binPath, schemaMap);
                
                if (localAch.Count > 0) {
                    AdvancedLogger.Log($"Steam encontrou {localAch.Count} conquistas traduzidas.");
                } else {
                    return 0;
                }

                string jsonPath = Path.Combine(_api.Paths.ExtensionsDataPath, SSGuid, "SuccessStory", $"{game.Id}.json");
                if (!File.Exists(jsonPath)) return 0;

                var attributes = File.GetAttributes(jsonPath);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(jsonPath, attributes & ~FileAttributes.ReadOnly);
                    AdvancedLogger.Log($"[DESBLOQUEADO] O arquivo JSON do jogo estava trancado e foi liberado.");
                }

                var db = JsonConvert.DeserializeObject<SuccessStoryDatabase>(File.ReadAllText(jsonPath));
                int updated = 0;

                foreach (var ach in db.Items)
                {
                    
                    if (ach.DateUnlocked.HasValue && ach.DateUnlocked.Value.Year > 2000) 
                        continue;

                    string keyToSearch = !string.IsNullOrWhiteSpace(ach.ApiName) ? ach.ApiName.Trim() : (ach.Name?.Trim() ?? "");
                    DateTime unlockDate = DateTime.MinValue;
                    bool found = false;

                    foreach (var kvp in localAch)
                    {
                        if (IsPerfectMatch(kvp.Key, keyToSearch))
                        {
                            unlockDate = kvp.Value;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        ach.DateUnlocked = unlockDate;
                        updated++;
                        AdvancedLogger.Log($"[MATCH CERTEIRO] Steam: '{keyToSearch}' -> Data: {unlockDate}");
                    }
                }

                if (updated > 0)
                {
                    File.WriteAllText(jsonPath, JsonConvert.SerializeObject(db, Formatting.Indented));
                    AdvancedLogger.Log($"=== SUCESSO: {updated} conquistas injetadas à força! ===");
                }
                
                return updated;
            }
            catch (Exception ex) { AdvancedLogger.Log($"ERRO: {ex.Message}"); return 0; }
        }

        private bool IsPerfectMatch(string localKey, string jsonApiName)
        {
            if (string.IsNullOrWhiteSpace(jsonApiName)) return false;

            if (string.Equals(localKey, jsonApiName, StringComparison.OrdinalIgnoreCase)) return true;

            if (jsonApiName.EndsWith("_" + localKey, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }
}