using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalAchievements.Services
{
    public enum PlaytimeSyncResult
    {
        Synced,
        SkippedNotHigher,
        NoSteamTime,
        NotSupported,
        Error
    }

    public class SteamPlaytimeService
    {
        private readonly IPlayniteAPI _api;
        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, CachedApps> appsCache = new Dictionary<string, CachedApps>(StringComparer.OrdinalIgnoreCase);
        private static bool warnedUnknownAccount;

        private class CachedApps
        {
            public DateTime WriteTime;
            public Dictionary<string, object> Apps;
        }

        private class AppTime
        {
            public ulong Minutes;
            public DateTime? LastPlayed;
        }

        public SteamPlaytimeService(IPlayniteAPI api)
        {
            _api = api;
        }

        public PlaytimeSyncResult Sync(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.GameId)) return PlaytimeSyncResult.NotSupported;

                string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(steamPath)) return PlaytimeSyncResult.NotSupported;

                string userdata = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdata)) return PlaytimeSyncResult.NotSupported;

                string me = SteamAccount.Resolve(_api, steamPath, out _);
                if (me == null)
                {
                    if (!warnedUnknownAccount)
                    {
                        warnedUnknownAccount = true;
                        AdvancedLogger.Log("[FAIL] Playtime sync skipped: could not tell which Steam account is yours (several accounts on this PC and none is the active one). Start Steam and log into your account.");
                    }
                    return PlaytimeSyncResult.NotSupported;
                }

                var mine = ReadAppTime(userdata, me, game.GameId);

                if (RepairForeignPlaytime(game, userdata, me, mine)) return PlaytimeSyncResult.Synced;

                ulong minutes = mine?.Minutes ?? 0;
                if (minutes == 0) return PlaytimeSyncResult.NoSteamTime;

                ulong steamSeconds = minutes * 60UL;
                if (steamSeconds <= game.Playtime) return PlaytimeSyncResult.SkippedNotHigher;

                ulong old = game.Playtime;
                game.Playtime = steamSeconds;
                if (mine.LastPlayed.HasValue && (!game.LastActivity.HasValue || game.LastActivity.Value < mine.LastPlayed.Value))
                    game.LastActivity = mine.LastPlayed.Value;

                _api.Database.Games.Update(game);

                AdvancedLogger.Log($"[HORAS] {game.Name}: {old / 60} min -> {minutes} min (Steam era maior).");
                return PlaytimeSyncResult.Synced;
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[HORAS] ERRO em {game?.Name}: {ex.Message}");
                return PlaytimeSyncResult.Error;
            }
        }

        private bool RepairForeignPlaytime(Game game, string userdata, string me, AppTime mine)
        {
            if (game.Playtime == 0) return false;

            ulong mineSeconds = (mine?.Minutes ?? 0) * 60UL;
            if (game.Playtime == mineSeconds) return false;

            foreach (var dir in Directory.GetDirectories(userdata))
            {
                string id = Path.GetFileName(dir);
                if (id == me) continue;

                var other = ReadAppTime(userdata, id, game.GameId);
                if (other == null || other.Minutes == 0 || other.Minutes * 60UL != game.Playtime) continue;

                ulong old = game.Playtime;
                game.Playtime = mineSeconds;
                game.LastActivity = mine?.LastPlayed;
                _api.Database.Games.Update(game);

                AdvancedLogger.Log($"[HORAS] {game.Name}: {old / 60} min came from another Steam account on this PC; reset to {mineSeconds / 60} min (your account).");
                return true;
            }

            return false;
        }

        private AppTime ReadAppTime(string userdata, string accountId, string appId)
        {
            var apps = GetApps(Path.Combine(userdata, accountId, "config", "localconfig.vdf"));
            var app = apps == null ? null : VdfText.Child(apps, appId);
            if (app == null) return null;

            var time = new AppTime();

            if (VdfText.Value(app, "Playtime") is string pt && ulong.TryParse(pt, out ulong minutes))
                time.Minutes = minutes;

            if (VdfText.Value(app, "LastPlayed") is string lp && long.TryParse(lp, out long unix) && unix > 946684800)
                time.LastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

            return time;
        }

        private Dictionary<string, object> GetApps(string cfg)
        {
            if (!File.Exists(cfg)) return null;

            DateTime write = File.GetLastWriteTimeUtc(cfg);
            lock (cacheLock)
            {
                if (appsCache.TryGetValue(cfg, out CachedApps cached) && cached.WriteTime == write) return cached.Apps;
            }

            try
            {
                string text;
                using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();

                var apps = VdfText.Find(VdfText.Parse(text), "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");

                lock (cacheLock)
                {
                    appsCache[cfg] = new CachedApps { WriteTime = write, Apps = apps };
                }
                return apps;
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[HORAS] Failed to read a Steam localconfig.vdf: {ex.Message}");
                return null;
            }
        }
    }

    internal static class VdfText
    {
        public static Dictionary<string, object> Parse(string text)
        {
            int pos = 0;
            return ParseBlock(text, ref pos, true);
        }

        private static Dictionary<string, object> ParseBlock(string s, ref int i, bool top)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = NextToken(s, ref i, out bool isBrace);
                if (key == null) return dict;
                if (isBrace) { if (key == "}") return dict; continue; }

                string val = NextToken(s, ref i, out isBrace);
                if (val == null) return dict;
                if (isBrace)
                {
                    if (val == "{") dict[key] = ParseBlock(s, ref i, false);
                    else return dict;
                }
                else dict[key] = val;
            }
        }

        private static string NextToken(string s, ref int i, out bool isBrace)
        {
            isBrace = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
            if (i >= s.Length) return null;

            if (s[i] == '{' || s[i] == '}') { isBrace = true; return s[i++].ToString(); }

            if (s[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i++]);
                }
                i++;
                return sb.ToString();
            }

            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') i++;
            return s.Substring(st, i - st);
        }

        public static Dictionary<string, object> Find(Dictionary<string, object> node, params string[] path)
        {
            var cur = node;
            foreach (var p in path)
            {
                cur = Child(cur, p);
                if (cur == null) return null;
            }
            return cur;
        }

        public static Dictionary<string, object> Child(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static object Value(Dictionary<string, object> node, string key)
            => node != null && node.TryGetValue(key, out var v) ? v : null;
    }
}
