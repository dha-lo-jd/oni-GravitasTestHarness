using System;
using System.IO;

namespace Gravitas.TestHarness.Core;

/// <summary>Tracks what the harness knows about the game's lifecycle and drives save loading.</summary>
public static class GameState
{
    /// <summary>True once the in-game scene has finished spawning (Game.LateUpdate fired OnSpawnComplete).</summary>
    public static bool SpawnComplete;

    /// <summary>Informational mod dialogs (e.g. "mods differ from the save") that the harness recorded and did not show.</summary>
    public static readonly System.Collections.Generic.List<object> SuppressedDialogs = new System.Collections.Generic.List<object>();

    /// <summary>Save most recently requested through <see cref="LoadSave"/>.</summary>
    public static string LoadRequested;

    public static string Screen()
    {
        if (Game.Instance != null && SpawnComplete && GameClock.Instance != null && SpeedControlScreen.Instance != null)
            return "playing";
        if (Game.Instance != null || LoadRequested != null)
            return "loading";
        return MainMenu.Instance != null ? "main_menu" : "unknown";
    }

    public static bool Ready => Screen() == "playing";

    /// <summary>
    /// Starts loading a save exactly as the main menu's "Resume" does. Returns immediately;
    /// poll <c>/status</c> until <c>ready</c>. Main thread only.
    /// </summary>
    public static void LoadSave(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Save file not found: " + path);
        if (MainMenu.Instance == null) throw new InvalidOperationException("Not at the main menu (screen=" + Screen() + ")");
        if (LoadRequested != null) throw new InvalidOperationException("A load is already in progress: " + LoadRequested);

        LoadRequested = path;
        UnityEngine.Debug.Log("[Gravitas.TestHarness] Loading save: " + path);
        KCrashReporter.MOST_RECENT_SAVEFILE = path;
        SaveLoader.SetActiveSaveFilePath(path);
        LoadingOverlay.Load(SaveLoader.LoadScene);
    }
}
