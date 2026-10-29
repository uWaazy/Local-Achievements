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

        public SteamPlaytimeService(IPlayniteAPI api)
        {
            _api = api;
        }

        public PlaytimeSyncResult Sync(Game game)
        {
            try
            {
                if (!TryGetSteamPlaytime(game.GameId, out ulong minutes, out DateTime? lastPlayed, out bool steamFound))
                    return steamFound ? PlaytimeSyncResult.NoSteamTime : PlaytimeSyncResult.NotSupported;

                if (minutes == 0) return PlaytimeSyncResult.NoSteamTime;

                ulong steamSeconds = minutes * 60UL;
                if (steamSeconds <= game.Playtime) return PlaytimeSyncResult.SkippedNotHigher;

                ulong old = game.Playtime;
                game.Playtime = steamSeconds;
                if (lastPlayed.HasValue && (!game.LastActivity.HasValue || game.LastActivity.Value < lastPlayed.Value))
                    game.LastActivity = lastPlayed.Value;

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

        private bool TryGetSteamPlaytime(string appId, out ulong minutes, out DateTime? lastPlayed, out bool steamFound)
        {
            minutes = 0; lastPlayed = null; steamFound = false;
            if (string.IsNullOrEmpty(appId)) return false;

            string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(steamPath)) return false;

            string userdata = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdata)) return false;

            steamFound = true;
            bool found = false;

            foreach (var cfg in Directory.GetDirectories(userdata).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(cfg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        text = sr.ReadToEnd();

                    var root = VdfText.Parse(text);
                    var apps = VdfText.Find(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                    var app = apps == null ? null : VdfText.Child(apps, appId);
                    if (app == null) continue;

                    if (VdfText.Value(app, "Playtime") is string pt && ulong.TryParse(pt, out ulong m) && m > 0)
                    {
                        found = true;
                        if (m > minutes) minutes = m;
                    }
                    if (VdfText.Value(app, "LastPlayed") is string lp && long.TryParse(lp, out long unix) && unix > 946684800)
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                        if (!lastPlayed.HasValue || dt > lastPlayed.Value) lastPlayed = dt;
                    }
                }
                catch (Exception ex) { AdvancedLogger.Log($"[HORAS] Falha lendo {cfg}: {ex.Message}"); }
            }
            return found;
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
