using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        private static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        private static string GetSteamPath()
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString().Replace('/', Path.DirectorySeparatorChar);
        }

        private static string Fail(Game game, string text)
        {
            AdvancedLogger.Log($"[FAIL] {game.Name} (AppID {game.GameId}): {text}");
            return text;
        }

        private static string Sample(IEnumerable<string> items, int count)
        {
            var list = items.Take(count).ToList();
            return list.Count == 0 ? "-" : string.Join(", ", list);
        }

        public int Sync(Game game)
        {
            return Sync(game, out _);
        }

        public int Sync(Game game, out string reason)
        {
            reason = null;
            try
            {
                AdvancedLogger.Log($"=== Iniciando Injeção Cirúrgica: {game.Name} ===");

                string steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) { reason = Fail(game, "Steam installation path not found in the Windows registry."); return 0; }

                string binDir = Path.Combine(steamPath, "appcache", "stats");
                if (!Directory.Exists(binDir)) { reason = Fail(game, $"Steam stats folder not found: {binDir}"); return 0; }

                var files = Directory.GetFiles(binDir, $"UserGameStats_*_{game.GameId}.bin");
                if (files.Length == 0)
                {
                    reason = Fail(game, $"No UserGameStats_*_{game.GameId}.bin file in the Steam stats folder. Steam has not created the stats file for this AppID yet; start the game once through Steam.");
                    return 0;
                }

                string binPath = files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                string schemaPath = Path.Combine(binDir, $"UserGameStatsSchema_{game.GameId}.bin");

                var schemaMap = VdfReader.LoadSchemaTranslation(schemaPath);
                var localAch = VdfReader.ExtractAchievements(binPath, schemaMap);

                if (localAch.Count > 0) {
                    AdvancedLogger.Log($"Steam encontrou {localAch.Count} conquistas traduzidas.");
                } else {
                    reason = Fail(game, $"No unlocked achievements could be read from {Path.GetFileName(binPath)} (schema entries: {schemaMap.Count}). Either nothing is unlocked yet or the file format was not recognized.");
                    return 0;
                }

                string jsonPath = Path.Combine(_api.Paths.ExtensionsDataPath, SSGuid, "SuccessStory", $"{game.Id}.json");
                if (!File.Exists(jsonPath))
                {
                    reason = Fail(game, $"SuccessStory has no data file for this game ({game.Id}.json). Open SuccessStory for this game and refresh its achievements first.");
                    return 0;
                }

                var attributes = File.GetAttributes(jsonPath);
                if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(jsonPath, attributes & ~FileAttributes.ReadOnly);
                    AdvancedLogger.Log($"[DESBLOQUEADO] O arquivo JSON do jogo estava trancado e foi liberado.");
                }

                var db = JsonConvert.DeserializeObject<SuccessStoryDatabase>(File.ReadAllText(jsonPath));
                if (db?.Items == null || db.Items.Count == 0)
                {
                    reason = Fail(game, "The SuccessStory data file has no achievement list.");
                    return 0;
                }

                int updated = 0;
                int locked = 0;

                foreach (var ach in db.Items)
                {
                    if (ach.DateUnlocked.HasValue && ach.DateUnlocked.Value.Year > 2000)
                        continue;

                    locked++;

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
                else if (locked > 0)
                {
                    var lockedNames = db.Items
                        .Where(a => !(a.DateUnlocked.HasValue && a.DateUnlocked.Value.Year > 2000))
                        .Select(a => !string.IsNullOrWhiteSpace(a.ApiName) ? a.ApiName.Trim() : (a.Name?.Trim() ?? ""));
                    reason = Fail(game, $"{localAch.Count} unlocked achievements were read from Steam, but none matched the {locked} locked SuccessStory achievements. Steam names: {Sample(localAch.Keys, 3)}. SuccessStory names: {Sample(lockedNames, 3)}.");
                }

                return updated;
            }
            catch (Exception ex)
            {
                reason = Fail(game, $"Unexpected error: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        public string Diagnose(Game game)
        {
            var sb = new StringBuilder();

            try
            {
                sb.AppendLine("=== Local Achievements diagnostic ===");
                sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Playnite: {_api.ApplicationInfo?.ApplicationVersion}");
                sb.AppendLine($"Plugin assembly: {typeof(SuccessStorySyncService).Assembly.GetName().Version}");
                sb.AppendLine($"Game: {game.Name}");
                sb.AppendLine($"Playnite Id: {game.Id}");
                sb.AppendLine($"GameId (AppID): {game.GameId}");
                sb.AppendLine($"Source: {game.Source?.Name ?? "-"}");

                string pluginNote = game.PluginId == Guid.Empty
                    ? " (EMPTY: the game may be stuck in the temporary hidden state)"
                    : game.PluginId == SteamPluginId ? " (Steam library plugin)" : " (not the Steam library plugin)";
                sb.AppendLine($"PluginId: {game.PluginId}{pluginNote}");
                sb.AppendLine($"InstallDirectory: {(string.IsNullOrEmpty(game.InstallDirectory) ? "-" : game.InstallDirectory)}");

                string steamPath = GetSteamPath();
                sb.AppendLine($"Steam path: {steamPath ?? "NOT FOUND"}");

                Dictionary<string, DateTime> localAch = new Dictionary<string, DateTime>();

                if (!string.IsNullOrEmpty(steamPath) && !string.IsNullOrEmpty(game.GameId))
                {
                    string luaPath = Path.Combine(steamPath, "config", "stplug-in", $"{game.GameId}.lua");
                    sb.AppendLine($"stplug-in lua: {(File.Exists(luaPath) ? "found" : "MISSING")}");

                    string binDir = Path.Combine(steamPath, "appcache", "stats");
                    sb.AppendLine($"Steam stats folder: {(Directory.Exists(binDir) ? "found" : "MISSING")}");

                    var statFiles = Directory.Exists(binDir) ? Directory.GetFiles(binDir, $"UserGameStats_*_{game.GameId}.bin") : new string[0];
                    sb.AppendLine($"UserGameStats files: {statFiles.Length}");
                    foreach (var f in statFiles)
                    {
                        var fi = new FileInfo(f);
                        sb.AppendLine($"  - {fi.Name} | {fi.Length} bytes | modified {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
                    }

                    string schemaPath = Path.Combine(binDir, $"UserGameStatsSchema_{game.GameId}.bin");
                    if (File.Exists(schemaPath))
                    {
                        var si = new FileInfo(schemaPath);
                        sb.AppendLine($"Schema file: found | {si.Length} bytes");
                    }
                    else sb.AppendLine("Schema file: MISSING");

                    var schemaMap = VdfReader.LoadSchemaTranslation(schemaPath);
                    sb.AppendLine($"Schema entries parsed: {schemaMap.Count}");
                    string schemaSample = Sample(schemaMap.Select(kv => kv.Key + "=" + kv.Value), 4);
                    sb.AppendLine("Schema sample: " + schemaSample);

                    if (statFiles.Length > 0)
                    {
                        string binPath = statFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                        localAch = VdfReader.ExtractAchievements(binPath, schemaMap);
                    }
                    sb.AppendLine($"Unlocked achievements read from Steam: {localAch.Count}");
                    sb.AppendLine($"Steam names sample: {Sample(localAch.Keys, 5)}");
                }

                string jsonPath = Path.Combine(_api.Paths.ExtensionsDataPath, SSGuid, "SuccessStory", $"{game.Id}.json");
                sb.AppendLine($"SuccessStory data file: {(File.Exists(jsonPath) ? "found" : "MISSING")}");

                if (File.Exists(jsonPath))
                {
                    var db = JsonConvert.DeserializeObject<SuccessStoryDatabase>(File.ReadAllText(jsonPath));
                    var items = db?.Items ?? new List<SuccessStoryAchievement>();
                    var lockedItems = items.Where(a => !(a.DateUnlocked.HasValue && a.DateUnlocked.Value.Year > 2000)).ToList();
                    sb.AppendLine($"SuccessStory achievements: {items.Count} total, {items.Count - lockedItems.Count} unlocked, {lockedItems.Count} locked");

                    Func<SuccessStoryAchievement, string> nameOf = a => !string.IsNullOrWhiteSpace(a.ApiName) ? a.ApiName.Trim() : (a.Name?.Trim() ?? "");
                    sb.AppendLine($"SuccessStory names sample: {Sample(items.Select(nameOf), 5)}");

                    int wouldMatch = lockedItems.Count(a => localAch.Keys.Any(k => IsPerfectMatch(k, nameOf(a))));
                    sb.AppendLine($"Locked achievements that would be matched now: {wouldMatch}");

                    var unmatchedLocal = localAch.Keys.Where(k => !items.Any(a => IsPerfectMatch(k, nameOf(a))));
                    sb.AppendLine($"Steam names with no SuccessStory match: {Sample(unmatchedLocal, 5)}");
                }

                if (!string.IsNullOrEmpty(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
                {
                    var hits = new List<string>();
                    int scanned = 0;
                    FindAchievementFiles(game.InstallDirectory, 0, 3, hits, ref scanned);
                    sb.AppendLine($"Achievement-like files in game folder: {(hits.Count == 0 ? "none found" : "")}");
                    foreach (var h in hits) sb.AppendLine($"  - {h.Substring(game.InstallDirectory.Length).TrimStart('\\')}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Diagnostic error: {ex.GetType().Name}: {ex.Message}");
            }

            return sb.ToString();
        }

        private static void FindAchievementFiles(string dir, int depth, int maxDepth, List<string> hits, ref int scanned)
        {
            if (depth > maxDepth || hits.Count >= 15 || scanned > 5000) return;

            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(dir))
                {
                    scanned++;
                    string name = Path.GetFileName(entry);
                    bool match = name.IndexOf("achiev", StringComparison.OrdinalIgnoreCase) >= 0
                              || name.IndexOf("ali213", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (match && hits.Count < 15) hits.Add(entry);
                    if (hits.Count >= 15 || scanned > 5000) return;
                }

                foreach (var sub in Directory.GetDirectories(dir))
                    FindAchievementFiles(sub, depth + 1, maxDepth, hits, ref scanned);
            }
            catch { }
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