using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalAchievements.Services
{
    public class SchemaAchievement
    {
        public string ApiName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Hidden { get; set; }
        public string IconUrl { get; set; }
        public string IconGrayUrl { get; set; }
    }

    public static class SteamSchemaReader
    {
        private const string ImageBase = "https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/";

        public static List<SchemaAchievement> ReadAchievements(string schemaPath, string appId, string steamLanguage)
        {
            var result = new List<SchemaAchievement>();
            if (!File.Exists(schemaPath)) return result;

            byte[] data;
            using (var fs = new FileStream(schemaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                data = ms.ToArray();
            }

            int pos = 0;
            var root = ParseBlock(data, ref pos);
            var app = root.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
            var stats = Child(app, "stats");
            if (stats == null) return result;

            foreach (var block in OrderedByNumber(stats))
            {
                var blockNode = block.Value as Dictionary<string, object>;
                if (blockNode == null) continue;
                if (!string.Equals(Text(blockNode, "type"), "ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase)) continue;

                var bits = Child(blockNode, "bits");
                if (bits == null) continue;

                foreach (var bit in OrderedByNumber(bits))
                {
                    var node = bit.Value as Dictionary<string, object>;
                    string apiName = Text(node, "name");
                    if (string.IsNullOrEmpty(apiName)) continue;

                    var display = Child(node, "display");
                    string title = Localized(Child(display, "name"), steamLanguage);
                    string description = Localized(Child(display, "desc"), steamLanguage);

                    result.Add(new SchemaAchievement
                    {
                        ApiName = apiName,
                        Name = string.IsNullOrEmpty(title) ? apiName : title,
                        Description = description ?? "",
                        Hidden = Text(display, "hidden") == "1",
                        IconUrl = ImageUrl(appId, Text(display, "icon")),
                        IconGrayUrl = ImageUrl(appId, Text(display, "icon_gray"))
                    });
                }
            }

            return result;
        }

        private static string ImageUrl(string appId, string file)
        {
            if (string.IsNullOrEmpty(file)) return "";
            if (file.IndexOf('.') < 0) file += ".jpg";
            return ImageBase + appId + "/" + file;
        }

        private static string Localized(Dictionary<string, object> node, string language)
        {
            if (node == null) return null;

            foreach (var key in new[] { language, "english" })
            {
                if (key != null && node.TryGetValue(key, out object value) && value is string s && s.Length > 0) return s;
            }

            return node.Where(kv => !kv.Key.Equals("token", StringComparison.OrdinalIgnoreCase))
                       .Select(kv => kv.Value as string)
                       .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }

        private static IEnumerable<KeyValuePair<string, object>> OrderedByNumber(Dictionary<string, object> node)
        {
            return node.OrderBy(kv => int.TryParse(kv.Key, out int n) ? n : int.MaxValue);
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> node, string key)
        {
            return node != null && node.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;
        }

        private static string Text(Dictionary<string, object> node, string key)
        {
            return node != null && node.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        private static Dictionary<string, object> ParseBlock(byte[] data, ref int pos)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (pos < data.Length)
            {
                byte type = data[pos++];
                if (type == 8) return dict;

                string key = ReadString(data, ref pos);

                switch (type)
                {
                    case 0:
                        dict[key] = ParseBlock(data, ref pos);
                        break;
                    case 1:
                        dict[key] = ReadString(data, ref pos);
                        break;
                    case 2:
                    case 4:
                    case 6:
                        dict[key] = BitConverter.ToInt32(data, pos);
                        pos += 4;
                        break;
                    case 3:
                        dict[key] = BitConverter.ToSingle(data, pos);
                        pos += 4;
                        break;
                    case 7:
                        dict[key] = BitConverter.ToUInt64(data, pos);
                        pos += 8;
                        break;
                    case 10:
                        dict[key] = BitConverter.ToInt64(data, pos);
                        pos += 8;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown VDF type {type} at offset {pos - 1}");
                }
            }

            return dict;
        }

        private static string ReadString(byte[] data, ref int pos)
        {
            int end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0) end = data.Length;
            string s = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = Math.Min(end + 1, data.Length);
            return s;
        }
    }
}
