using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LocalAchievements.Services
{
    public class VdfReader
    {
        public static Dictionary<string, string> LoadSchemaTranslation(string schemaPath)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(schemaPath)) return map;

            try
            {
                byte[] bytes = File.ReadAllBytes(schemaPath);
                List<string> tokens = new List<string>();
                int i = 0;

                while (i < bytes.Length)
                {
                    if (bytes[i] >= 32 && bytes[i] <= 126)
                    {
                        int start = i;
                        while (i < bytes.Length && bytes[i] >= 32 && bytes[i] <= 126) i++;
                        string str = Encoding.ASCII.GetString(bytes, start, i - start).Trim();
                        if (!string.IsNullOrEmpty(str)) tokens.Add(str);
                    }
                    else i++;
                }

                string currentStatId = "";
                for (int j = 0; j < tokens.Count - 2; j++)
                {
                    if (tokens[j + 1].Equals("bits", StringComparison.OrdinalIgnoreCase) && IsNumeric(tokens[j]))
                    {
                        currentStatId = tokens[j];
                    }

                    if (tokens[j + 1].Equals("name", StringComparison.OrdinalIgnoreCase) && IsNumeric(tokens[j]))
                    {
                        string bitId = tokens[j];
                        string apiName = tokens[j + 2];
                        
                        if (!string.IsNullOrEmpty(currentStatId)) {
                            map[currentStatId + "_" + bitId] = apiName;
                        }
                        
                        if (!map.ContainsKey(bitId) && apiName.Length < 100) {
                            map[bitId] = apiName;
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public static Dictionary<string, DateTime> ExtractAchievements(string caminhoArquivo, Dictionary<string, string> schemaMap)
        {
            var results = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            bool hasSchema = schemaMap != null && schemaMap.Count > 0;
            
            try
            {
                byte[] fileBytes = File.ReadAllBytes(caminhoArquivo);
                DateTime fallbackDate = File.GetLastWriteTime(caminhoArquivo);

                int i = 0;
                string currentStatId = "";
                bool inAchTimes = false;

                while (i < fileBytes.Length - 5)
                {
                    if (fileBytes[i] == 0x08) 
                    {
                        inAchTimes = false;
                        i++;
                        continue;
                    }

                    byte type = fileBytes[i];
                    if (type == 0x00 || type == 0x01 || type == 0x02)
                    {
                        int nameStart = i + 1;
                        int nameEnd = nameStart;
                        while (nameEnd < fileBytes.Length && fileBytes[nameEnd] != 0x00) nameEnd++;
                        if (nameEnd >= fileBytes.Length) break;

                        string key = Encoding.ASCII.GetString(fileBytes, nameStart, nameEnd - nameStart).Trim();
                        
                        if (type == 0x00) 
                        {
                            if (key.Equals("AchievementTimes", StringComparison.OrdinalIgnoreCase)) {
                                inAchTimes = true;
                            } else if (IsNumeric(key)) {
                                currentStatId = key;
                            }
                            i = nameEnd + 1;
                        }
                        else if (type == 0x01) 
                        {
                            int valStart = nameEnd + 1;
                            int valEnd = valStart;
                            while (valEnd < fileBytes.Length && fileBytes[valEnd] != 0x00) valEnd++;
                            i = valEnd + 1;
                        }
                        else if (type == 0x02) 
                        {
                            int valStart = nameEnd + 1;
                            if (valStart + 4 <= fileBytes.Length)
                            {
                                int value = BitConverter.ToInt32(fileBytes, valStart);
                                
                                if (value > 0 && !IsReserved(key))
                                {
                                    string mapKey = key;
                                    if (inAchTimes && !string.IsNullOrEmpty(currentStatId)) {
                                        mapKey = currentStatId + "_" + key;
                                    }

                                    DateTime unlockDate = value > 946684800 ? DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime : fallbackDate;
                                    string finalName = key;

                                    if (hasSchema && schemaMap.ContainsKey(mapKey)) {
                                        finalName = schemaMap[mapKey];
                                    } else if (hasSchema && schemaMap.ContainsKey(key)) {
                                        finalName = schemaMap[key];
                                    }

                                    if (!results.ContainsKey(finalName)) {
                                        results.Add(finalName, unlockDate);
                                    } else if (value > 946684800) {
                                        results[finalName] = unlockDate; 
                                    }
                                }
                                i = valStart + 4;
                            } else {
                                i++;
                            }
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            catch { }
            return results;
        }

        private static bool IsNumeric(string str) => int.TryParse(str, out _);

        private static bool IsReserved(string word)
        {
            string[] r = { "crc", "PendingChanges", "state", "schema", "AchievementTimes", "data", "stats", "playtime" };
            foreach (var s in r) if (word.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}