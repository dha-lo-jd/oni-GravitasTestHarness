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
            if (!HarnessConfig.AllowGameOver) DisableGameOver();
            __instance.OnSpawnComplete += () =>
            {
                GameState.SpawnComplete = true;
                Debug.Log("[Gravitas.TestHarness] Game spawn complete");
            };
        }
    }

    private static void DisableGameOver()
    {
        try
        {
            // GenericGameSettings.disableGameOver has a private setter; the type lives outside the referenced assemblies' public surface.
            var type = Reflect.FindType("GenericGameSettings");
            var instance = type.GetProperty("instance").GetValue(null, null);
            type.GetProperty("disableGameOver").GetSetMethod(true).Invoke(instance, new object[] { true });
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[Gravitas.TestHarness] Could not disable game over: " + e.Message);
        }
    }

    [HarmonyPatch(typeof(Game), "OnCleanUp")]
    public static class GameCleanUp
    {
        public static void Postfix()
        {
            GameState.SpawnComplete = false;
            TimeControl.Reset();
        }
    }

    /// <summary>
    /// Informational mod dialogs (no confirm/cancel handler, e.g. "mods differ from the save", safe mode notice)
    /// would block the UI. Record them (see /status) and do not show them. Dialogs that carry a handler
    /// (restart prompts) are left alone because suppressing them would change what the game does.
    /// </summary>
    [HarmonyPatch(typeof(KMod.Manager), nameof(KMod.Manager.Dialog))]
    public static class ModDialog
    {
        public static bool Prefix(string title, string text, System.Action on_confirm, System.Action on_cancel,
            System.Action on_configurable_clicked)
        {
            var informational = on_confirm == null && on_cancel == null && on_configurable_clicked == null;
            GameState.SuppressedDialogs.Add(new { title, text, suppressed = informational, realtime = Time.realtimeSinceStartup });
            Debug.Log("[Gravitas.TestHarness] Mod dialog " + (informational ? "suppressed" : "shown") + ": " + title + " / " + text);
            return !informational;
        }
    }

    [HarmonyPatch(typeof(GameClock), nameof(GameClock.Sim33ms))]
    public static class ClockStep
    {
        public static void Postfix()
        {
            TimeControl.OnSimStep();
        }
    }
}
