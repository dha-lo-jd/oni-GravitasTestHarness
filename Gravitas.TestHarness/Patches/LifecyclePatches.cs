using Gravitas.TestHarness.Core;
using HarmonyLib;
using UnityEngine;

namespace Gravitas.TestHarness.Patches;

/// <summary>Hooks only the minimum of game internals: lifecycle signals for /status and the startup auto-load.</summary>
internal static class LifecyclePatches
{
    private static bool _autoLoadDone;

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    public static class MainMenuSpawn
    {
        public static void Postfix()
        {
            GameState.SpawnComplete = false;
            GameState.LoadRequested = null;

            var path = HarnessConfig.AutoLoadPath;
            if (_autoLoadDone || path == null) return;
            _autoLoadDone = true;
            try { GameState.LoadSave(path); }
            catch (System.Exception e) { Debug.LogError("[Gravitas.TestHarness] Auto-load failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    public static class GameSpawn
    {
        public static void Postfix(Game __instance)
        {
            __instance.OnSpawnComplete += () =>
            {
                GameState.SpawnComplete = true;
                Debug.Log("[Gravitas.TestHarness] Game spawn complete");
            };
        }
    }

    [HarmonyPatch(typeof(Game), "OnCleanUp")]
    public static class GameCleanUp
    {
        public static void Postfix()
        {
            GameState.SpawnComplete = false;
        }
    }
}
