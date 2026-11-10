using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Gravitas.TestHarness.Core;

/// <summary>POST /screenshot: captures the game view at the end of the next frame and writes a PNG.</summary>
public static class Screenshot
{
    public static Pending Capture(string name)
    {
        var dir = HarnessConfig.OutputDir;
        Directory.CreateDirectory(dir);
        if (string.IsNullOrWhiteSpace(name)) name = "shot-" + System.DateTime.Now.ToString("HHmmss-fff");
        name = Path.GetFileName(name); // no directory traversal
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
        var path = Path.Combine(dir, name);

        var pending = new Pending { TimeoutMs = 30000 };
        MainThreadDispatcher.Instance.StartCoroutine(Run(pending, path));
        return pending;
    }

    private static IEnumerator Run(Pending pending, string path)
    {
        yield return new WaitForEndOfFrame();
        try
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var png = tex.EncodeToPNG();
            var w = tex.width;
            var h = tex.height;
            UnityEngine.Object.Destroy(tex);
            File.WriteAllBytes(path, png);
            pending.Complete(new { path, width = w, height = h, bytes = png.Length });
        }
        catch (Exception e)
        {
            pending.Fail(e);
        }
    }
}
