using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace NightRunners.WheelSupport.Config
{
    // Standalone JSON serializer/deserializer with zero external dependencies.
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return new Parser(json).ParseValue();
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _pos;

            public Parser(string s) { _s = s; _pos = 0; }

            private void SkipWs()
            {
                while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
            }

            private char Peek() { SkipWs(); return _pos < _s.Length ? _s[_pos] : '\0'; }
            private char Next() { SkipWs(); return _pos < _s.Length ? _s[_pos++] : '\0'; }

            public object ParseValue()
            {
                char c = Peek();
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't' || c == 'f') return ParseBool();
                if (c == 'n') return ParseNull();
                if (char.IsDigit(c) || c == '-') return ParseNumber();
                throw new Exception($"Unexpected token '{c}' at position {_pos}");
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Next(); // '{'
                while (true)
                {
                    char c = Peek();
                    if (c == '}') { Next(); break; }
                    if (c == ',') { Next(); continue; }
                    string key = ParseString();
                    char colon = Next();
                    if (colon != ':') throw new Exception($"Expected ':' at position {_pos}");
                    object val = ParseValue();
                    dict[key] = val;
                }
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Next(); // '['
                while (true)
                {
                    char c = Peek();
                    if (c == ']') { Next(); break; }
                    if (c == ',') { Next(); continue; }
                    list.Add(ParseValue());
                }
                return list;
            }

            private string ParseString()
            {
                char quote = Next(); // '"'
                if (quote != '"') throw new Exception($"Expected string quote at position {_pos}");
                var sb = new StringBuilder();
                while (_pos < _s.Length)
                {
                    char c = _s[_pos++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (_pos >= _s.Length) break;
                        char esc = _s[_pos++];
                        switch (esc)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_pos + 4 <= _s.Length)
                                {
                                    string hex = _s.Substring(_pos, 4);
                                    _pos += 4;
                                    sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                }
                                break;
                            default:
                                sb.Append(esc);
                                break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                throw new Exception("Unterminated string in JSON");
            }

            private bool ParseBool()
            {
                if (_s.Substring(_pos).StartsWith("true", StringComparison.OrdinalIgnoreCase))
                {
                    _pos += 4;
                    return true;
                }
                if (_s.Substring(_pos).StartsWith("false", StringComparison.OrdinalIgnoreCase))
                {
                    _pos += 5;
                    return false;
                }
                throw new Exception($"Expected boolean at position {_pos}");
            }

            private object ParseNull()
            {
                if (_s.Substring(_pos).StartsWith("null", StringComparison.OrdinalIgnoreCase))
                {
                    _pos += 4;
                    return null;
                }
                throw new Exception($"Expected null at position {_pos}");
            }

            private object ParseNumber()
            {
                int start = _pos;
                if (_s[_pos] == '-') _pos++;
                while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.' || _s[_pos] == 'e' || _s[_pos] == 'E' || _s[_pos] == '+' || _s[_pos] == '-'))
                {
                    _pos++;
                }
                string numStr = _s.Substring(start, _pos - start);
                if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
                {
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                }
                else
                {
                    if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                }
                return numStr;
            }
        }
    }

    // Handles saving, loading, importing, exporting, and listing wheel configuration profiles as JSON.
    internal static class ConfigProfileManager
    {
        public static string ProfilesDirectory
        {
            get
            {
                string dir = Path.Combine(Paths.ConfigPath, "com.flaze.nightrunners.wheelsupport.profiles");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        public static List<string> GetProfileNames()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(ProfilesDirectory))
                {
                    foreach (var file in Directory.GetFiles(ProfilesDirectory, "*.json"))
                    {
                        list.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"GetProfileNames error: {ex.Message}");
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "profile";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) < 0 && c != ' ') sb.Append(c);
                else sb.Append('_');
            }
            string res = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(res) ? "profile" : res;
        }

        public static string SerializeConfigToString(ConfigFile file, string profileName)
        {
            string cleanName = SanitizeFileName(profileName);
            var sections = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in file)
            {
                var def = kvp.Key;
                var entry = kvp.Value;
                if (entry?.BoxedValue == null) continue;

                if (!sections.TryGetValue(def.Section, out var secDict))
                {
                    secDict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[def.Section] = secDict;
                }
                secDict[def.Key] = Convert.ToString(entry.BoxedValue, CultureInfo.InvariantCulture) ?? "";
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{EscapeString(cleanName)}\",");
            sb.AppendLine($"  \"modVersion\": \"{EscapeString(WheelSupportPlugin.PluginVersion)}\",");
            sb.AppendLine($"  \"exportedAt\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\",");
            sb.AppendLine("  \"sections\": {");

            int sIdx = 0;
            foreach (var secKvp in sections)
            {
                sIdx++;
                sb.AppendLine($"    \"{EscapeString(secKvp.Key)}\": {{");
                int kIdx = 0;
                foreach (var entryKvp in secKvp.Value)
                {
                    kIdx++;
                    string comma = kIdx < secKvp.Value.Count ? "," : "";
                    sb.AppendLine($"      \"{EscapeString(entryKvp.Key)}\": \"{EscapeString(entryKvp.Value)}\"{comma}");
                }
                string secComma = sIdx < sections.Count ? "," : "";
                sb.AppendLine($"    }}{secComma}");
            }

            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static bool ExportProfile(ConfigFile file, string profileName, out string message)
        {
            message = "";
            if (file == null) { message = "ConfigFile is null"; return false; }

            try
            {
                string cleanName = SanitizeFileName(profileName);
                string filePath = Path.Combine(ProfilesDirectory, cleanName + ".json");
                string json = SerializeConfigToString(file, cleanName);

                File.WriteAllText(filePath, json, Encoding.UTF8);
                message = $"Exported '{cleanName}.json'";
                ModLog.Info($"Exported profile '{cleanName}' to {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ModLog.Warn($"ExportProfile error: {ex.Message}");
                return false;
            }
        }

        public static bool ExportProfileToPath(ConfigFile file, string profileName, string targetFilePath, out string message)
        {
            message = "";
            if (file == null) { message = "ConfigFile is null"; return false; }
            if (string.IsNullOrEmpty(targetFilePath)) { message = "Target path is empty"; return false; }

            try
            {
                string cleanPath = targetFilePath.Trim();
                if (!cleanPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    cleanPath += ".json";

                string dir = Path.GetDirectoryName(cleanPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string cleanName = string.IsNullOrEmpty(profileName)
                    ? Path.GetFileNameWithoutExtension(cleanPath)
                    : profileName;

                string json = SerializeConfigToString(file, cleanName);
                File.WriteAllText(cleanPath, json, Encoding.UTF8);

                message = $"Exported to '{Path.GetFileName(cleanPath)}'";
                ModLog.Info($"Exported profile '{cleanName}' to custom path {cleanPath}");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ModLog.Warn($"ExportProfileToPath error: {ex.Message}");
                return false;
            }
        }

        public static bool ImportProfile(ConfigFile file, string profileNameOrPath, out string message)
        {
            return ImportProfileFromPath(file, profileNameOrPath, false, out message);
        }

        public static bool ImportProfileFromPath(ConfigFile file, string profileNameOrPath, bool saveToProfilesFolder, out string message)
        {
            message = "";
            if (file == null) { message = "ConfigFile is null"; return false; }
            if (string.IsNullOrEmpty(profileNameOrPath)) { message = "Profile path is empty"; return false; }

            try
            {
                string path = profileNameOrPath;
                if (!File.Exists(path))
                {
                    string withExt = profileNameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? profileNameOrPath
                        : profileNameOrPath + ".json";
                    path = Path.Combine(ProfilesDirectory, withExt);
                }

                if (!File.Exists(path))
                {
                    message = $"File not found: {Path.GetFileName(path)}";
                    return false;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
                if (root == null)
                {
                    message = "Invalid JSON structure in profile file";
                    return false;
                }

                // Build a lookup of existing entries (section + key) once. ConfigFile is
                // enumerable but has no TryGetValue in this BepInEx build.
                var byDef = new Dictionary<string, ConfigEntryBase>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in file)
                {
                    byDef[kvp.Key.Section + "|" + kvp.Key.Key] = kvp.Value;
                }

                int appliedCount = 0;
                if (root.TryGetValue("sections", out var secObj) && secObj is Dictionary<string, object> sections)
                {
                    foreach (var secKvp in sections)
                    {
                        string section = secKvp.Key;
                        if (secKvp.Value is Dictionary<string, object> entries)
                        {
                            foreach (var entryKvp in entries)
                            {
                                string key = entryKvp.Key;
                                string valStr = Convert.ToString(entryKvp.Value, CultureInfo.InvariantCulture);
                                if (byDef.TryGetValue(section + "|" + key, out var cfgEntry))
                                {
                                    try
                                    {
                                        object converted = Convert.ChangeType(valStr, cfgEntry.SettingType, CultureInfo.InvariantCulture);
                                        cfgEntry.BoxedValue = converted;
                                        appliedCount++;
                                    }
                                    catch (Exception parseEx)
                                    {
                                        ModLog.Warn($"Could not parse {section}/{key} = '{valStr}': {parseEx.Message}");
                                    }
                                }
                            }
                        }
                    }
                }

                file.Save();

                // If loaded from an external directory (e.g. Desktop / Downloads / USB),
                // copy or save it into the profiles directory so it stays in the saved profiles list.
                if (saveToProfilesFolder)
                {
                    try
                    {
                        string profileName = Path.GetFileNameWithoutExtension(path);
                        string localPath = Path.Combine(ProfilesDirectory, SanitizeFileName(profileName) + ".json");
                        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(localPath, json, Encoding.UTF8);
                            ModLog.Info($"Saved imported profile copy to {localPath}");
                        }
                    }
                    catch (Exception copyEx)
                    {
                        ModLog.Warn($"Could not copy imported profile to profiles dir: {copyEx.Message}");
                    }
                }

                message = $"Imported '{Path.GetFileNameWithoutExtension(path)}' ({appliedCount} settings applied)";
                ModLog.Info($"Imported profile from {path} with {appliedCount} settings.");
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                ModLog.Warn($"ImportProfile error: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteProfile(string profileName, out string message)
        {
            message = "";
            try
            {
                string cleanName = SanitizeFileName(profileName);
                string path = Path.Combine(ProfilesDirectory, cleanName + ".json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    message = $"Deleted profile '{cleanName}.json'";
                    ModLog.Info(message);
                    return true;
                }
                message = $"Profile '{cleanName}.json' not found";
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static void OpenProfilesFolder()
        {
            try
            {
                string dir = ProfilesDirectory;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                ModLog.Info($"Opened profiles folder in Explorer: {dir}");
            }
            catch (Exception ex)
            {
                ModLog.Warn($"OpenProfilesFolder error: {ex.Message}");
            }
        }

        public static bool ResetToDefaults(ConfigFile file, out string message)
        {
            message = "";
            if (file == null) { message = "ConfigFile is null"; return false; }
            try
            {
                int resetCount = 0;
                foreach (var kvp in file)
                {
                    var entry = kvp.Value;
                    if (entry != null && entry.DefaultValue != null)
                    {
                        entry.BoxedValue = entry.DefaultValue;
                        resetCount++;
                    }
                }
                file.Save();
                message = $"Reset {resetCount} settings to defaults";
                ModLog.Info(message);
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
