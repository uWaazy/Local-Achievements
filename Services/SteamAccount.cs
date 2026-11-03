using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.IO;
using System.Linq;

namespace LocalAchievements.Services
{
    public static class SteamAccount
    {
        private const long SteamId64Base = 76561197960265728L;
        private const string SteamLibraryPluginId = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private static readonly object cacheLock = new object();
        private static string cachedId;
        private static string cachedSource;
        private static DateTime cachedAt = DateTime.MinValue;

        public static string Resolve(IPlayniteAPI api, string steamPath, out string source)
        {
            lock (cacheLock)
            {
                if (DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(30))
                {
                    source = cachedSource;
                    return cachedId;
                }

                string id = FromPlayniteSettings(api, steamPath);
                source = "playnite-steam-settings";

                if (id == null) { id = FromActiveUser(steamPath); source = "active-user"; }
                if (id == null) { id = FromLoginUsers(steamPath); source = "most-recent-login"; }
                if (id == null) { id = FromSingleFolder(steamPath); source = "single-userdata-folder"; }
                if (id == null) source = "unknown";

                cachedId = id;
                cachedSource = source;
                cachedAt = DateTime.UtcNow;
                return id;
            }
        }

        private static bool Exists(string steamPath, string accountId)
        {
            return !string.IsNullOrEmpty(accountId) && Directory.Exists(Path.Combine(steamPath, "userdata", accountId));
        }

        private static string FromSteamId64(string steamPath, string value)
        {
            if (!long.TryParse(value, out long id64) || id64 <= SteamId64Base) return null;
            string accountId = (id64 - SteamId64Base).ToString();
            return Exists(steamPath, accountId) ? accountId : null;
        }

        private static string FromPlayniteSettings(IPlayniteAPI api, string steamPath)
        {
            try
            {
                string file = Path.Combine(api.Paths.ExtensionsDataPath, SteamLibraryPluginId, "config.json");
                if (!File.Exists(file)) return null;

                var settings = JObject.Parse(File.ReadAllText(file));
                string value = settings["UserId"]?.ToString();
                return FromSteamId64(steamPath, value);
            }
            catch { return null; }
        }

        private static string FromActiveUser(string steamPath)
        {
            try
            {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
                if (value is int active && active > 0 && Exists(steamPath, active.ToString())) return active.ToString();
            }
            catch { }
            return null;
        }

        private static string FromLoginUsers(string steamPath)
        {
            try
            {
                string file = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (!File.Exists(file)) return null;

                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();

                var users = VdfText.Find(VdfText.Parse(text), "users");
                if (users == null) return null;

                foreach (var entry in users)
                {
                    var user = entry.Value as System.Collections.Generic.Dictionary<string, object>;
                    if (user != null && VdfText.Value(user, "MostRecent") as string == "1")
                        return FromSteamId64(steamPath, entry.Key);
                }
            }
            catch { }
            return null;
        }

        private static string FromSingleFolder(string steamPath)
        {
            try
            {
                string userdata = Path.Combine(steamPath, "userdata");
                if (!Directory.Exists(userdata)) return null;

                var folders = Directory.GetDirectories(userdata)
                    .Select(Path.GetFileName)
                    .Where(n => long.TryParse(n, out long v) && v > 0)
                    .ToList();

                return folders.Count == 1 ? folders[0] : null;
            }
            catch { return null; }
        }
    }
}
