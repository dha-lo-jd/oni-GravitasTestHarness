using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// POST /invoke: calls one method by reflection (no expression evaluation).
/// <code>{"method": "Namespace.Type.StaticMethod", "args": [1, "a"]}</code>
/// <code>{"target": "Game.Instance", "method": "SomeMethod", "args": []}</code>
/// "target" is a static path as in /query (it may end in a list index); the method is then an instance method of the value.
/// Arguments are converted to the parameter types with Newtonsoft (enums by name or number). Private methods are allowed.
/// </summary>
public static class Invoke
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static object Run(JObject body)
    {
        if (body == null) throw new ArgumentException("body is required");
        var methodName = (string)body["method"];
        if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("'method' is required");
        var args = (body["args"] as JArray) ?? new JArray();

        object target = null;
        Type type;
        BindingFlags flags;
        if (body["target"] != null)
        {
            target = Reflect.ReadStaticPath((string)body["target"]);
            if (target == null) throw new InvalidOperationException("target is null: " + (string)body["target"]);
            type = target.GetType();
            flags = All;
        }
        else
        {
            var dot = methodName.LastIndexOf('.');
            if (dot < 0) throw new ArgumentException("without 'target', 'method' must be Type.Method");
            type = Reflect.FindType(methodName.Substring(0, dot)) ?? throw new ArgumentException("Type not found: " + methodName.Substring(0, dot));
            methodName = methodName.Substring(dot + 1);
            flags = All & ~BindingFlags.Instance;
        }

        var candidates = type.GetMethods(flags | BindingFlags.FlattenHierarchy)
            .Where(m => m.Name == methodName && !m.ContainsGenericParameters)
            .Where(m => args.Count <= m.GetParameters().Length && args.Count >= m.GetParameters().Count(p => !p.IsOptional))
            .ToList();
        if (candidates.Count == 0)
            throw new ArgumentException("No method '" + methodName + "' on " + type.FullName + " taking " + args.Count + " argument(s)");

        Exception lastConversion = null;
        foreach (var m in candidates)
        {
            object[] values;
            try { values = Convert(m, args); }
            catch (Exception e) { lastConversion = e; continue; }
            try
            {
                var result = m.Invoke(m.IsStatic ? null : target, values);
                return new { method = m.DeclaringType.FullName + "." + m.Name, result = m.ReturnType == typeof(void) ? null : Reflect.ToJson(result) };
            }
            catch (TargetInvocationException e)
            {
                throw new InvalidOperationException((e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message, e.InnerException ?? e);
            }
        }
        throw new ArgumentException("Arguments do not fit any overload of " + methodName + ": " + lastConversion?.Message);
    }

    private static object[] Convert(MethodInfo m, JArray args)
    {
        var ps = m.GetParameters();
        var values = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (i < args.Count) values[i] = args[i].Type == JTokenType.Null ? null : args[i].ToObject(ps[i].ParameterType);
            else values[i] = ps[i].DefaultValue is DBNull ? Type.Missing : ps[i].DefaultValue;
        }
        return values;
    }
}
