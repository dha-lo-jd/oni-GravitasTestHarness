using HarmonyLib;
using KMod;
using UnityEngine;

namespace Gravitas.TestHarness;

public sealed class TestHarnessMod : UserMod2
{
    public const string Version = "0.1.0";

    internal static string modPath;

    public override void OnLoad(Harmony harmony)
    {
        base.OnLoad(harmony);
        modPath = path;
        harmony.PatchAll(typeof(TestHarnessMod).Assembly);
        Debug.Log("[Gravitas.TestHarness] Loaded v" + Version);
    }
}
