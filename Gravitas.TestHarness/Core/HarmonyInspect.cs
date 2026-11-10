using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Gravitas.TestHarness.Core;

/// <summary>Diagnostics over Harmony's patch table; call through POST /invoke ("Gravitas.TestHarness.Core.HarmonyInspect.Duplicates").</summary>
public static class HarmonyInspect
{
    /// <summary>
    /// Patch methods registered more than once on the same target method (the signature of a double PatchAll).
    /// <paramref name="ownerContains"/> limits the report to Harmony ids containing that text (empty = all).
    /// </summary>
    public static JToken Duplicates(string ownerContains = "")
    {
        var duplicates = new List<object>();
        var perOwner = new SortedDictionary<string, int>();
        var methods = Harmony.GetAllPatchedMethods().ToList();
        foreach (var target in methods)
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) continue;
            foreach (var (kind, list) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) })
            {
                foreach (var p in list)
                {
                    perOwner[p.owner] = perOwner.TryGetValue(p.owner, out var n) ? n + 1 : 1;
                }
                foreach (var g in list.GroupBy(p => p.owner + "|" + p.PatchMethod.DeclaringType?.FullName + "::" + p.PatchMethod.Name).Where(g => g.Count() > 1))
                {
                    var first = g.First();
                    if (!string.IsNullOrEmpty(ownerContains) && first.owner.IndexOf(ownerContains, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    duplicates.Add(new
                    {
                        target = target.DeclaringType?.FullName + "." + target.Name,
                        kind,
                        patch = first.PatchMethod.DeclaringType?.FullName + "::" + first.PatchMethod.Name,
                        owner = first.owner,
                        count = g.Count(),
                    });
                }
            }
        }
        return JToken.FromObject(new { patched_methods = methods.Count, patches_per_owner = perOwner, duplicate_count = duplicates.Count, duplicates });
    }

    /// <summary>Every patch whose Harmony id contains <paramref name="ownerContains"/>, as "owner kind target <- patch" lines.</summary>
    public static JToken Patches(string ownerContains)
    {
        var lines = new List<string>();
        foreach (var target in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) continue;
            foreach (var (kind, list) in new[] { ("prefix", info.Prefixes), ("postfix", info.Postfixes), ("transpiler", info.Transpilers), ("finalizer", info.Finalizers) })
                foreach (var p in list.Where(p => p.owner.IndexOf(ownerContains ?? "", System.StringComparison.OrdinalIgnoreCase) >= 0))
                    lines.Add(p.owner + " " + kind + " " + target.DeclaringType?.FullName + "." + target.Name + " <- " + p.PatchMethod.DeclaringType?.FullName + "::" + p.PatchMethod.Name);
        }
        return new JArray(lines.OrderBy(l => l));
    }
}
