using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Gravitas.TestHarness.Core;

/// <summary>Finds <c>[HarnessTest]</c> methods in all loaded assemblies and runs them on the main thread.</summary>
public static class TestRunner
{
    private const int DefaultTimeoutSeconds = 120;
    private static bool _running;
    private static bool _cancel;

    public sealed class TestCase
    {
        public string Id;
        public string[] Tags;
        public int TimeoutSeconds;
        public bool RequiresGame;
        public MethodInfo Method;
        public string Invalid;
        public bool Manual => Tags.Contains("manual");
    }

    private static readonly string[] SkippedAssemblies =
        { "System", "mscorlib", "netstandard", "Mono.", "Unity", "UnityEngine", "Assembly-CSharp", "0Harmony", "Newtonsoft", "Microsoft" };

    public static List<TestCase> Discover()
    {
        var list = new List<TestCase>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name;
            if (SkippedAssemblies.Any(s => asmName.StartsWith(s))) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            catch (Exception) { continue; }

            foreach (var type in types)
            {
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception) { continue; }
                foreach (var m in methods)
                {
                    object attr;
                    try { attr = m.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "HarnessTestAttribute"); }
                    catch (Exception) { continue; }
                    if (attr == null) continue;

                    var name = Prop<string>(attr, "Name");
                    var timeout = Prop<int>(attr, "TimeoutSeconds");
                    var requires = attr.GetType().GetProperty("RequiresGame") == null || Prop<bool>(attr, "RequiresGame");
                    var tc = new TestCase
                    {
                        Id = string.IsNullOrEmpty(name) ? type.FullName + "." + m.Name : name,
                        Tags = (Prop<string>(attr, "Tags") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                        TimeoutSeconds = timeout > 0 ? timeout : DefaultTimeoutSeconds,
                        RequiresGame = requires,
                        Method = m,
                    };
                    if (m.GetParameters().Length != 0) tc.Invalid = "test methods take no parameters";
                    else if (m.ReturnType != typeof(void) && m.ReturnType != typeof(IEnumerator))
                        tc.Invalid = "test methods return void or IEnumerator";
                    list.Add(tc);
                }
            }
        }
        return list.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    private static T Prop<T>(object attr, string name)
    {
        var p = attr.GetType().GetProperty(name);
        if (p == null) return default;
        try { return (T)Convert.ChangeType(p.GetValue(attr, null), typeof(T)); }
        catch (Exception) { return default; }
    }

    public static object List(JObject body)
    {
        var all = Discover();
        return new
        {
            count = all.Count,
            tests = all.Select(t => new { id = t.Id, tags = t.Tags, requires_game = t.RequiresGame, timeout_s = t.TimeoutSeconds, invalid = t.Invalid }),
        };
    }

    private static Regex Glob(string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Body: <c>names</c> (exact ids), <c>pattern</c> (glob on id, '*' and '?'; without wildcards = substring),
    /// <c>tags</c> (any match), <c>timeout_s</c> (whole run, default 1800). No selector = every test except tag "manual".
    /// </summary>
    public static Pending Run(JObject body)
    {
        if (_running) throw new InvalidOperationException("A test run is already in progress");
        var names = (body?["names"] as JArray)?.Select(t => (string)t).ToList();
        var pattern = (string)body?["pattern"];
        var tags = (body?["tags"] as JArray)?.Select(t => (string)t).ToList();
        var selective = names != null || pattern != null || tags != null;

        var selected = Discover().Where(t =>
        {
            if (!selective) return !t.Manual;
            if (names != null && names.Contains(t.Id)) return true;
            if (pattern != null && (pattern.Contains("*") || pattern.Contains("?")
                    ? Glob(pattern).IsMatch(t.Id)
                    : t.Id.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return tags != null && t.Tags.Any(tg => tags.Contains(tg));
        }).ToList();

        var timeoutMs = body?["timeout_s"] != null ? (int)((float)body["timeout_s"] * 1000) : 1800000;
        var pending = new Pending { TimeoutMs = timeoutMs, OnTimeout = () => { _cancel = true; TimeControl.Cancel(); } };
        _running = true;
        _cancel = false;
        MainThreadDispatcher.Instance.StartCoroutine(RunAll(selected, pending));
        return pending;
    }

    private static IEnumerator RunAll(List<TestCase> tests, Pending pending)
    {
        var results = new JArray();
        var sw = Stopwatch.StartNew();
        var logs = new List<string>();
        Application.LogCallback onLog = (msg, stack, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                logs.Add(type + ": " + msg);
        };
        Application.logMessageReceived += onLog;
        try
        {
            foreach (var t in tests)
            {
                if (_cancel) break;
                logs.Clear();
                var result = new JObject { ["id"] = t.Id };
                results.Add(result);
                var tsw = Stopwatch.StartNew();
                yield return RunOne(t, result);
                result["duration_ms"] = tsw.ElapsedMilliseconds;
                if (logs.Count > 0) result["log_errors"] = new JArray(logs.Take(20));
            }
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            _running = false;
        }

        if (_cancel) { yield break; } // Pending already timed out on the HTTP side
        int Count(string s) => results.Count(r => (string)r["status"] == s);
        pending.Complete(new
        {
            total = results.Count,
            passed = Count("passed"),
            failed = Count("failed"),
            errors = Count("error") + Count("timeout"),
            skipped = Count("skipped"),
            duration_ms = sw.ElapsedMilliseconds,
            results,
        });
    }

    private static void Set(JObject r, string status, string message = null, Exception e = null)
    {
        r["status"] = status;
        if (message != null) r["message"] = message;
        if (e != null) r["exception"] = e.ToString();
    }

    private static IEnumerator RunOne(TestCase t, JObject result)
    {
        if (t.Invalid != null) { Set(result, "error", t.Invalid); yield break; }
        if (t.RequiresGame && !GameState.Ready) { Set(result, "skipped", "no game is loaded (screen=" + GameState.Screen() + ")"); yield break; }

        var deadline = Time.realtimeSinceStartup + t.TimeoutSeconds;
        var stack = new Stack<IEnumerator>();

        object ret = null;
        Exception failure = null;
        try { ret = t.Method.Invoke(null, null); }
        catch (TargetInvocationException e) { failure = e.InnerException ?? e; }
        catch (Exception e) { failure = e; }
        if (failure != null) { Fail(result, failure); yield break; }
        if (ret is IEnumerator root) stack.Push(root);

        while (stack.Count > 0)
        {
            if (_cancel) { Set(result, "error", "run cancelled"); yield break; }
            if (Time.realtimeSinceStartup > deadline) { TimeControl.Cancel(); Set(result, "timeout", "exceeded " + t.TimeoutSeconds + " s"); yield break; }

            object current = null;
            var finished = false;
            try
            {
                if (stack.Peek().MoveNext()) current = stack.Peek().Current;
                else { stack.Pop(); finished = true; }
            }
            catch (Exception e) { failure = e; }
            if (failure != null) { Fail(result, failure); yield break; }
            if (finished) continue;

            if (current is IEnumerator nested) { stack.Push(nested); continue; }
            if (current is AdvanceRequest req)
            {
                Pending p = null;
                try { p = TimeControl.Advance(req.Seconds, req.Speed, DefaultTimeoutSeconds * 1000); }
                catch (Exception e) { failure = e; }
                if (failure != null) { Fail(result, failure); yield break; }
                while (!p.Done.IsSet)
                {
                    if (Time.realtimeSinceStartup > deadline || _cancel)
                    {
                        TimeControl.Cancel();
                        Set(result, "timeout", "exceeded " + t.TimeoutSeconds + " s while advancing game time");
                        yield break;
                    }
                    yield return null;
                }
                continue;
            }
            yield return current; // null = next frame; Unity YieldInstructions pass through
        }
        Set(result, "passed");
    }

    private static void Fail(JObject result, Exception e)
    {
        if (e.GetType().Name == "HarnessAssertionException") Set(result, "failed", e.Message);
        else Set(result, "error", e.GetType().Name + ": " + e.Message, e);
    }
}
