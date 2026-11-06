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
using System.Text.RegularExpressions;

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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Description { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string UrlUnlocked { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string UrlLocked { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? IsHidden { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? Percent { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? GamerScore { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? CategoryOrder { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Category { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ParentCategory { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string CategoryRpcs3 { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string CategoryShadPS4 { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? NoRarety { get; set; }
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }
    }

    public class SuccessStorySyncService
    {
        private readonly IPlayniteAPI _api;
        private const string SSGuid = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private static readonly Dictionary<string, DateTime> ownWrites = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ownWritesLock = new object();

        public string SuccessStoryDirectory => Path.Combine(_api.Paths.ExtensionsDataPath, SSGuid, "SuccessStory");

        private static void RecordOwnWrite(string path)
        {
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(path);
                lock (ownWritesLock) { ownWrites[path] = written; }
            }
            catch { }
        }

        public static bool IsOwnWrite(string path)
        {
            try
            {
                DateTime current = File.GetLastWriteTimeUtc(path);
                lock (ownWritesLock) { return ownWrites.TryGetValue(path, out DateTime recorded) && recorded == current; }
            }
            catch { return false; }
        }

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

        private static readonly object statusLock = new object();
        private static Dictionary<string, string> lastStatus;

        private static string StatusFilePath()
        {
            string logPath = AdvancedLogger.LogFilePath;
            return string.IsNullOrEmpty(logPath) ? null : Path.Combine(Path.GetDirectoryName(logPath), "last_status.json");
        }

        private static bool IsNewStatus(Game game, string text)
        {
            string file = StatusFilePath();
            if (file == null) return true;

            lock (statusLock)
            {
                if (lastStatus == null)
                {
                    try
                    {
                        if (File.Exists(file))
                            lastStatus = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                    }
                    catch { }
                    if (lastStatus == null) lastStatus = new Dictionary<string, string>();
                }

                string key = game.Id.ToString();
                if (lastStatus.TryGetValue(key, out string old) && old == text) return false;

                lastStatus[key] = text;
                try { File.WriteAllText(file, JsonConvert.SerializeObject(lastStatus)); } catch { }
                return true;
            }
        }

        private static void ClearStatus(Game game)
        {
            string file = StatusFilePath();
            if (file == null) return;

            lock (statusLock)
            {
                if (lastStatus == null || !lastStatus.Remove(game.Id.ToString())) return;
                try { File.WriteAllText(file, JsonConvert.SerializeObject(lastStatus)); } catch { }
            }
        }

        private static string MaskAccount(string fileName)
        {
            return Regex.Replace(fileName ?? "", @"UserGameStats_\d+_", "UserGameStats_<account>_");
        }

        private static string Fail(Game game, string text, bool expected = false)
        {
            if (IsNewStatus(game, text))
                AdvancedLogger.Log($"[{(expected ? "INFO" : "FAIL")}] {game.Name} (AppID {game.GameId}): {text}");
            return text;
        }

        private static string NameOf(SuccessStoryAchievement a)
        {
            return !string.IsNullOrWhiteSpace(a.ApiName) ? a.ApiName.Trim() : (a.Name?.Trim() ?? "");
        }

        private static string Sample(IEnumerable<string> items, int count)
        {
            var list = items.Take(count).ToList();
            return list.Count == 0 ? "-" : string.Join(", ", list);
        }

        private static readonly Dictionary<string, string> SteamLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pt_BR", "brazilian" }, { "pt_PT", "portuguese" }, { "en_US", "english" }, { "en_GB", "english" },
            { "es_ES", "spanish" }, { "es_MX", "latam" }, { "de_DE", "german" }, { "fr_FR", "french" },
            { "it_IT", "italian" }, { "ru_RU", "russian" }, { "pl_PL", "polish" }, { "tr_TR", "turkish" },
            { "uk_UA", "ukrainian" }, { "zh_CN", "schinese" }, { "zh_TW", "tchinese" }, { "ko_KR", "koreana" },
            { "ja_JP", "japanese" }, { "nl_NL", "dutch" }, { "cs_CZ", "czech" }, { "hu_HU", "hungarian" }
        };

        private string GetSteamLanguage()
        {
            string code = _api.ApplicationSettings?.Language;
            return code != null && SteamLanguages.TryGetValue(code, out string language) ? language : "english";
        }

        private List<SuccessStoryAchievement> BuildItemsFromSchema(Game game, string schemaPath)
        {
            var items = new List<SuccessStoryAchievement>();

            try
            {
                foreach (var a in SteamSchemaReader.ReadAchievements(schemaPath, game.GameId, GetSteamLanguage()))
                {
                    items.Add(new SuccessStoryAchievement
                    {
                        Name = a.Name,
                        ApiName = a.ApiName,
                        DateUnlocked = null,
                        Description = a.Description,
                        UrlUnlocked = a.IconUrl,
                        UrlLocked = a.IconGrayUrl,
                        IsHidden = a.Hidden,
                        Percent = 100,
                        GamerScore = 0,
                        CategoryOrder = 0,
                        Category = "",
                        ParentCategory = "",
                        CategoryRpcs3 = "",
                        CategoryShadPS4 = "",
                        NoRarety = true
                    });
                }
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[FAIL] {game.Name} (AppID {game.GameId}): could not read the Steam schema: {ex.GetType().Name}: {ex.Message}");
            }

            return items;
        }

        private static string PickStatsFile(string[] files, string accountId, string appId)
        {
            if (files.Length == 0) return null;
            if (string.IsNullOrEmpty(accountId))
                return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();

            string expected = $"UserGameStats_{accountId}_{appId}.bin";
            return files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));
        }

        public int Sync(Game game)
        {
            return Sync(game, out _);
        }

        private static readonly object syncLock = new object();

        public int Sync(Game game, out string reason)
        {
            lock (syncLock)
            {
                return SyncCore(game, out reason);
            }
        }

        private int SyncCore(Game game, out string reason)
        {
            reason = null;
            try
            {
                string steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) { reason = Fail(game, "Steam installation path not found in the Windows registry."); return 0; }

                string binDir = Path.Combine(steamPath, "appcache", "stats");
                if (!Directory.Exists(binDir)) { reason = Fail(game, $"Steam stats folder not found: {binDir}"); return 0; }

                var files = Directory.GetFiles(binDir, $"UserGameStats_*_{game.GameId}.bin");
                if (files.Length == 0)
                {
                    reason = Fail(game, $"No UserGameStats_*_{game.GameId}.bin file in the Steam stats folder. Steam has not created the stats file for this AppID yet; start the game once through Steam.", true);
                    return 0;
                }

                string me = SteamAccount.Resolve(_api, steamPath, out _);
                string binPath = PickStatsFile(files, me, game.GameId);
                if (binPath == null)
                {
                    reason = Fail(game, "No stats file for your Steam account yet (other accounts on this PC have one, but they are ignored). Start the game once through Steam.", true);
                    return 0;
                }

                string schemaPath = Path.Combine(binDir, $"UserGameStatsSchema_{game.GameId}.bin");

                var schemaMap = VdfReader.LoadSchemaTranslation(schemaPath);
                var localAch = VdfReader.ExtractAchievements(binPath, schemaMap);

                if (localAch.Count == 0)
                {
                    reason = Fail(game, $"No unlocked achievements found in {MaskAccount(Path.GetFileName(binPath))} (schema entries: {schemaMap.Count}). Normal if nothing is unlocked yet; otherwise the file format may not be recognized.", true);
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
                if (db == null)
                {
                    reason = Fail(game, "The SuccessStory data file could not be read.");
                    return 0;
                }

                bool builtFromSchema = false;
                if (db.Items == null || db.Items.Count == 0)
                {
                    var built = BuildItemsFromSchema(game, schemaPath);
                    if (built.Count == 0)
                    {
                        reason = Fail(game, $"SuccessStory has an empty achievement list for this game and the Steam schema could not be used to rebuild it. Steam has {localAch.Count} unlocked locally.");
                        return 0;
                    }

                    db.Items = built;
                    builtFromSchema = true;
                    AdvancedLogger.Log($"[REBUILD] {game.Name} (AppID {game.GameId}): SuccessStory list was empty; rebuilt {built.Count} achievements from the Steam schema.");
                }

                int updated = 0;
                var matches = new List<string>();

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
                        matches.Add($"'{keyToSearch}' -> {unlockDate:yyyy-MM-dd HH:mm:ss}");
                    }
                }

                if (updated > 0 || builtFromSchema)
                {
                    File.WriteAllText(jsonPath, JsonConvert.SerializeObject(db, Formatting.Indented));
                    RecordOwnWrite(jsonPath);
                    if (updated <= 10)
                        foreach (var m in matches) AdvancedLogger.Log($"[MATCH] {game.Name}: {m}");
                    AdvancedLogger.Log($"[OK] {game.Name}: {updated} achievements injected into SuccessStory.");
                }
                else
                {
                    var unmatched = localAch.Keys.Where(k => !db.Items.Any(a => IsPerfectMatch(k, NameOf(a)))).ToList();
                    if (unmatched.Count > 0)
                    {
                        reason = Fail(game, $"{unmatched.Count} of {localAch.Count} unlocked Steam achievements have no equivalent in SuccessStory. Steam names: {Sample(unmatched, 3)}. SuccessStory names: {Sample(db.Items.Select(a => NameOf(a)), 3)}.");
                    }
                }

                if (reason == null) ClearStatus(game);
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
                sb.AppendLine($"Plugin version: {PluginInfo.Version}");
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
                        sb.AppendLine($"  - {MaskAccount(fi.Name)} | {fi.Length} bytes | modified {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
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
                        string me = SteamAccount.Resolve(_api, steamPath, out string accountSource);
                        sb.AppendLine($"Steam account detection: {accountSource}");
                        string binPath = PickStatsFile(statFiles, me, game.GameId);
                        sb.AppendLine($"Stats file used: {(binPath == null ? "none (no file for your account)" : MaskAccount(Path.GetFileName(binPath)))}");
                        if (binPath != null) localAch = VdfReader.ExtractAchievements(binPath, schemaMap);
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