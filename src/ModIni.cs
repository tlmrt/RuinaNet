// RuinaNet - tiny ini store.
//
// The file lives next to the mod itself, so both the game and a human editing
// notepad reach the same place:
//   <game>\LibraryOfRuina_Data\Mods\RuinaNet\RuinaNet.ini
//
// Kept deliberately dumb: read once into a flat "section.key" dictionary,
// rewrite the whole file on change.  Nothing here can throw into the game --
// a broken or read-only ini just means the mod falls back to defaults.
//
// Compiles with the in-box C# 5 compiler.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RuinaNet
{
    internal static class ModIni
    {
        private static Dictionary<string, string> _values;
        private static bool _dirty;

        private static string FilePath()
        {
            try
            {
                return Path.Combine(Application.dataPath, "Mods/RuinaNet/RuinaNet.ini");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Load()
        {
            if (_values != null)
            {
                return;
            }
            _values = new Dictionary<string, string>();
            string path = FilePath();
            if (path == null || !File.Exists(path))
            {
                return;
            }
            try
            {
                string section = "";
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    {
                        continue;
                    }
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = section + "." + line.Substring(0, eq).Trim().ToLowerInvariant();
                    _values[key] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ini] load failed: " + e.Message);
            }
        }

        public static string GetString(string section, string key, string fallback)
        {
            Load();
            string k = (section + "." + key).ToLowerInvariant();
            string v;
            if (_values.TryGetValue(k, out v) && v.Length > 0)
            {
                return v;
            }
            return fallback;
        }

        public static float GetFloat(string section, string key, float fallback)
        {
            string s = GetString(section, key, null);
            float v;
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return fallback;
        }

        public static bool GetBool(string section, string key, bool fallback)
        {
            string s = GetString(section, key, null);
            if (s == null)
            {
                return fallback;
            }
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on")
            {
                return true;
            }
            if (s == "0" || s == "false" || s == "no" || s == "off")
            {
                return false;
            }
            return fallback;
        }

        public static void Set(string section, string key, string value)
        {
            Load();
            string k = (section + "." + key).ToLowerInvariant();
            string old;
            if (_values.TryGetValue(k, out old) && old == value)
            {
                return;
            }
            _values[k] = value;
            _dirty = true;
            Save();
        }

        public static void SetFloat(string section, string key, float value)
        {
            Set(section, key, value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        public static void SetBool(string section, string key, bool value)
        {
            Set(section, key, value ? "true" : "false");
        }

        private static void Save()
        {
            if (!_dirty)
            {
                return;
            }
            string path = FilePath();
            if (path == null)
            {
                return;
            }
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                List<string> keys = new List<string>(_values.Keys);
                keys.Sort(StringComparer.Ordinal);
                List<string> outLines = new List<string>();
                outLines.Add("; RuinaNet settings -- written by the mod, safe to edit by hand.");
                string current = null;
                for (int i = 0; i < keys.Count; i++)
                {
                    int dot = keys[i].IndexOf('.');
                    string sec = dot > 0 ? keys[i].Substring(0, dot) : "";
                    string key = dot > 0 ? keys[i].Substring(dot + 1) : keys[i];
                    if (sec != current)
                    {
                        outLines.Add("");
                        outLines.Add("[" + sec + "]");
                        current = sec;
                    }
                    outLines.Add(key + " = " + _values[keys[i]]);
                }
                outLines.Add("");
                File.WriteAllLines(path, outLines.ToArray());
                _dirty = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ini] save failed: " + e.Message);
            }
        }
    }
}
