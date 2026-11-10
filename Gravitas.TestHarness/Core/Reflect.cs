using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Gravitas.TestHarness.Core;

/// <summary>Reflection helpers behind /query: type lookup, member paths and JSON conversion.</summary>
public static class Reflect
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

    /// <summary>Finds a type by full name or simple name (Assembly-CSharp preferred). Null if not found.</summary>
    public static Type FindType(string name)
    {
        if (TypeCache.TryGetValue(name, out var cached)) return cached;
        var found = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            catch (Exception) { continue; }
            found.AddRange(types.Where(t => t.FullName == name || t.Name == name));
        }
        if (found.Count == 0) return null;
        if (found.Count > 1)
        {
            var preferred = found.Where(t => t.Assembly.GetName().Name.StartsWith("Assembly-CSharp")).ToList();
            if (preferred.Count == 1) found = preferred;
            else
                throw new ArgumentException("Ambiguous type '" + name + "': " +
                    string.Join(", ", found.Take(6).Select(t => t.FullName)) + ". Use the full name.");
        }
        TypeCache[name] = found[0];
        return found[0];
    }

    /// <summary>Evaluates "a.b[2].c" starting at an instance.</summary>
    public static object ReadPath(object root, string path)
    {
        var cur = root;
        foreach (var token in path.Split('.'))
        {
            if (cur == null) return null;
            cur = Step(cur, cur.GetType(), token, false);
        }
        return cur;
    }

    /// <summary>Evaluates "Namespace.Type.StaticMember.more" (type = longest matching prefix).</summary>
    public static object ReadStaticPath(string path)
    {
        var parts = path.Split('.');
        for (var n = parts.Length - 1; n >= 1; n--)
        {
            var type = FindType(string.Join(".", parts.Take(n)));
            if (type == null) continue;
            object cur = null;
            var first = true;
            foreach (var token in parts.Skip(n))
            {
                if (first) { cur = Step(null, type, token, true); first = false; }
                else if (cur == null) return null;
                else cur = Step(cur, cur.GetType(), token, false);
            }
            return cur;
        }
        throw new ArgumentException("Cannot resolve a type from path '" + path + "'");
    }

    private static object Step(object target, Type type, string token, bool staticOnly)
    {
        var name = token;
        int? index = null;
        var br = token.IndexOf('[');
        if (br >= 0 && token.EndsWith("]"))
        {
            name = token.Substring(0, br);
            index = int.Parse(token.Substring(br + 1, token.Length - br - 2));
        }

        object value = target;
        if (name.Length > 0)
        {
            var flags = All | BindingFlags.FlattenHierarchy;
            if (staticOnly) flags &= ~BindingFlags.Instance;
            value = null;
            var ok = false;
            for (var t = type; t != null && !ok; t = t.BaseType)
            {
                var f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) { value = f.GetValue(f.IsStatic ? null : target); ok = true; break; }
                var p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    var getter = p.GetGetMethod(true);
                    value = getter.Invoke(getter.IsStatic ? null : target, null);
                    ok = true;
                }
            }
            if (!ok) throw new ArgumentException("No field or property '" + name + "' on " + type.FullName);
        }

        if (index.HasValue)
        {
            if (value is IList list) value = list[index.Value];
            else if (value is IEnumerable en) value = en.Cast<object>().ElementAt(index.Value);
            else throw new ArgumentException("'" + token + "': value is not indexable");
        }
        return value;
    }

    public static JToken ToJson(object v, int depth = 0)
    {
        if (v == null) return JValue.CreateNull();
        var t = v.GetType();
        if (v is string || t.IsPrimitive || v is decimal)
        {
            if (v is float f && (float.IsNaN(f) || float.IsInfinity(f))) return f.ToString();
            if (v is double d && (double.IsNaN(d) || double.IsInfinity(d))) return d.ToString();
            return new JValue(v);
        }
        if (t.IsEnum) return v.ToString();
        if (v is UnityEngine.Object uo)
        {
            if (uo == null) return JValue.CreateNull(); // destroyed Unity object
            var o = new JObject { ["_type"] = t.FullName, ["name"] = uo.name, ["id"] = uo.GetInstanceID() };
            return o;
        }
        if (v is IDictionary dict)
        {
            var o = new JObject();
            var n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (n++ >= 100) { o["_truncated"] = true; break; }
                o[Convert.ToString(e.Key)] = depth < 4 ? ToJson(e.Value, depth + 1) : Convert.ToString(e.Value);
            }
            return o;
        }
        if (v is IEnumerable en)
        {
            var arr = new JArray();
            foreach (var item in en)
            {
                if (arr.Count >= 100) { arr.Add("<truncated>"); break; }
                arr.Add(depth < 4 ? ToJson(item, depth + 1) : new JValue(Convert.ToString(item)));
            }
            return arr;
        }
        if (depth >= 2) return new JObject { ["_type"] = t.FullName, ["_str"] = SafeToString(v) };

        var obj = new JObject { ["_type"] = t.FullName };
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
            obj[f.Name] = Safe(() => ToJson(f.GetValue(v), depth + 1));
        // Structs (Vector3, Color, ...) expose derived properties such as normalized; fields are the data.
        if (!t.IsValueType)
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (p.CanRead && p.GetIndexParameters().Length == 0)
                    obj[p.Name] = Safe(() => ToJson(p.GetValue(v, null), depth + 1));
        return obj;
    }

    private static JToken Safe(Func<JToken> f)
    {
        try { return f(); }
        catch (Exception e) { return "<error: " + (e.InnerException ?? e).Message + ">"; }
    }

    private static string SafeToString(object v)
    {
        try { return v.ToString(); }
        catch (Exception) { return "<error>"; }
    }
}
