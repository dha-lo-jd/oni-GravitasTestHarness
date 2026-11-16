using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using KMod;
using Gravitas.TestHarness.Core;

namespace Gravitas.TestHarness;

/// <summary>
/// Conformance tests that apply to every mod, so a mod needs no test code of its own to get them: select them with the tag
/// <c>conformance</c>. They run against the mods named by <c>-harness-mods</c> (the probe passes the project's required_mods).
/// What they catch: a mod that did not load, Harmony patches applied twice, and a .po that is out of step with the code
/// (a stale or missing key, or a translation that was not applied in the running game).
/// </summary>
internal static class Conformance
{
    private static List<Mod> Targets()
    {
        var wanted = HarnessConfig.TargetMods;
        if (wanted.Count == 0) return new List<Mod>();
        var mods = Global.Instance.modManager.mods;
        var found = mods.Where(m => wanted.Contains(m.staticID)).ToList();
        var missing = wanted.Where(w => found.All(m => m.staticID != w)).ToList();
        if (missing.Count > 0)
            throw new HarnessAssertionException("target mod(s) not found by the game: " + string.Join(", ", missing));
        return found;
    }

    [HarnessTest(Name = "conformance.mods_loaded", Tags = "conformance", RequiresGame = false)]
    public static void ModsLoaded()
    {
        foreach (var mod in Targets())
        {
            if (mod.loaded_mod_data == null || mod.loaded_mod_data.dlls == null || mod.loaded_mod_data.dlls.Count == 0)
                throw new HarnessAssertionException(mod.staticID + " has no loaded assembly");
        }
    }

    // UserMod2.OnLoad already runs PatchAll; a second call applies every patch twice.
    [HarnessTest(Name = "conformance.patches_applied_once", Tags = "conformance", RequiresGame = false)]
    public static void PatchesAppliedOnce()
    {
        var problems = new List<string>();
        foreach (var mod in Targets())
        {
            var data = mod.loaded_mod_data;
            if (data == null || data.harmony == null || data.patched_methods == null) continue;
            var owner = data.harmony.Id;
            foreach (var method in data.patched_methods)
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                    foreach (var g in list.Where(p => p.owner == owner).GroupBy(p => p.PatchMethod).Where(g => g.Count() > 1))
                        problems.Add(mod.staticID + ": " + g.Key.DeclaringType?.Name + "." + g.Key.Name + " is applied " + g.Count()
                                     + " times to " + method.DeclaringType?.Name + "." + method.Name);
            }
        }
        if (problems.Count > 0) throw new HarnessAssertionException(string.Join("; ", problems));
    }

    // translations/ja.po must match the LocStrings the mod declares (in its STRINGS classes), in both directions, and be applied
    // when the game runs in Japanese. A LocString whose key was never assigned was not registered for translation at all.
    [HarnessTest(Name = "conformance.translations", Tags = "conformance", RequiresGame = false)]
    public static void Translations()
    {
        var code = Localization.GetLocale()?.Code ?? "";
        var japanese = code.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var mod in Targets())
        {
            var declared = new Dictionary<string, string>();
            foreach (var asm in mod.loaded_mod_data?.dlls ?? new List<System.Reflection.Assembly>())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types.Where(InStrings))
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(LocString)) continue;
                        var loc = (LocString)f.GetValue(null);
                        if (loc == null) continue;
                        var key = loc.key.String;
                        if (string.IsNullOrEmpty(key)) problems.Add(mod.staticID + ": " + t.FullName + "." + f.Name + " was not registered for translation (no key)");
                        else
                        {
                            // The game keys a LocString from the STRINGS class down ("STRINGS.UI.X"); the .po prefixes the mod's namespace.
                            var ns = t.Namespace ?? (t.DeclaringType?.Namespace ?? "");
                            declared[ns.Length > 0 && !key.StartsWith(ns + ".") ? ns + "." + key : key] = loc.text;
                        }
                    }
            }

            var path = Path.Combine(mod.ContentPath, "translations", "ja.po");
            if (!File.Exists(path)) continue; // no translation shipped; the static audit reports whether one is expected
            var po = ParsePo(File.ReadAllText(path, Encoding.UTF8));
            if (po.Count == 0)
            {
                problems.Add(mod.staticID + ": ja.po has no entries");
                continue;
            }

            foreach (var kv in po)
            {
                string text;
                if (!declared.TryGetValue(kv.Key, out text))
                {
                    problems.Add(mod.staticID + ": ja.po key " + kv.Key + " matches no LocString in the code (stale or renamed; the code declares e.g. " + declared.Keys.FirstOrDefault() + ")");
                    continue;
                }
                if (japanese && text != kv.Value.msgstr)
                    problems.Add(mod.staticID + ": " + kv.Key + " was not translated in the running game ('" + Short(text) + "' != '" + Short(kv.Value.msgstr) + "')");
                else if (!japanese && code == "" && text != kv.Value.msgid)
                    problems.Add(mod.staticID + ": " + kv.Key + " differs from the English msgid in ja.po ('" + Short(text) + "' != '" + Short(kv.Value.msgid) + "')");
            }
            foreach (var key in declared.Keys)
                if (!po.ContainsKey(key))
                    problems.Add(mod.staticID + ": " + key + " has no entry in ja.po");
        }
        if (problems.Count > 0)
            throw new HarnessAssertionException(problems.Count + " problem(s): " + string.Join(" | ", problems.Take(8)));
    }

    private static bool InStrings(Type t)
    {
        for (var x = t; x != null; x = x.DeclaringType)
            if (x.Name == "STRINGS") return true;
        return false;
    }

    private static string Short(string s)
    {
        if (s == null) return "null";
        return (s.Length > 40 ? s.Substring(0, 40) + "..." : s).Replace("\n", "\\n");
    }

    private struct PoEntry
    {
        public string msgid, msgstr;
    }

    /// <summary>Minimal .po reader: msgctxt / msgid / msgstr with continuation lines and the escapes \n \r \t \" \\.</summary>
    private static Dictionary<string, PoEntry> ParsePo(string text)
    {
        var result = new Dictionary<string, PoEntry>();
        string ctxt = null, id = null, str = null, current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '\uFEFF');
            if (line.Length == 0)
            {
                Flush(result, ref ctxt, ref id, ref str, ref current);
                continue;
            }
            if (line.StartsWith("#")) continue;
            string body;
            if (line.StartsWith("msgctxt "))
            {
                Flush(result, ref ctxt, ref id, ref str, ref current);
                current = "ctxt";
                body = line.Substring(8);
            }
            else if (line.StartsWith("msgid "))
            {
                if (current != "ctxt") Flush(result, ref ctxt, ref id, ref str, ref current);
                current = "id";
                body = line.Substring(6);
            }
            else if (line.StartsWith("msgstr "))
            {
                current = "str";
                body = line.Substring(7);
            }
            else body = line;
            var value = Unquote(body);
            if (value == null) continue;
            switch (current)
            {
                case "ctxt": ctxt = (ctxt ?? "") + value; break;
                case "id": id = (id ?? "") + value; break;
                case "str": str = (str ?? "") + value; break;
            }
        }
        Flush(result, ref ctxt, ref id, ref str, ref current);
        return result;
    }

    private static void Flush(Dictionary<string, PoEntry> result, ref string ctxt, ref string id, ref string str, ref string current)
    {
        if (!string.IsNullOrEmpty(ctxt) && id != null && str != null) result[ctxt] = new PoEntry { msgid = id, msgstr = str };
        ctxt = id = str = current = null;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') return null;
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length - 1)
            {
                sb.Append(s[i]);
                continue;
            }
            switch (s[++i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }
}
