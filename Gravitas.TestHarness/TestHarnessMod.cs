using Gravitas.TestHarness.Core;
using Gravitas.TestHarness.Http;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace Gravitas.TestHarness;

public sealed class TestHarnessMod : UserMod2
{
    public const string Version = "0.3.0";
    public const int ProtocolVersion = 1;

    internal static string modPath;

    public override void OnLoad(Harmony harmony)
    {
        base.OnLoad(harmony);
        modPath = path;
        // UserMod2.OnLoad (base) already runs harmony.PatchAll(assembly); patching again would apply every patch twice.

        MainThreadDispatcher.Install();
        try
        {
            var server = new HarnessServer();
            Endpoints.Register(server);
            server.Start(HarnessConfig.Port);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Gravitas.TestHarness] Failed to start HTTP server: " + e);
        }

        Debug.Log("[Gravitas.TestHarness] Loaded v" + Version);
    }
}
