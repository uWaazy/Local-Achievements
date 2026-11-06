using System.IO;
using System.Text.RegularExpressions;

namespace LocalAchievements.Services
{
    public static class PluginInfo
    {
        private static string version;

        public static string Version
        {
            get
            {
                if (version == null) version = ReadVersion();
                return version;
            }
        }

        private static string ReadVersion()
        {
            try
            {
                string directory = Path.GetDirectoryName(typeof(PluginInfo).Assembly.Location);
                string file = Path.Combine(directory, "extension.yaml");
                if (!File.Exists(file)) return "unknown";

                var match = Regex.Match(File.ReadAllText(file), @"(?m)^Version:\s*(\S+)");
                return match.Success ? match.Groups[1].Value : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
