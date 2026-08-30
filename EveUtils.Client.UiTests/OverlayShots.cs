using System;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Renders an overlay window to a PNG so the result can be gone and looked at, not only asserted on — the lesson
/// this project has learned rather more than once, and one that counts double for a window whose entire purpose is
/// how fast it reads.
///
/// Set <c>EVEUTILS_SHOT_DIR</c> to collect the frames somewhere you can open them; without it they go to the temp
/// directory and are simply overwritten next run.
/// </summary>
internal static class OverlayShots
{
    /// <summary>Render <paramref name="window"/> and return a hash of the pixels, so two states can be compared
    /// without a baseline image in the repo.</summary>
    public static string Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var directory = Environment.GetEnvironmentVariable("EVEUTILS_SHOT_DIR");
        var path = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory, name + ".png");

        // Avalonia offers no non-obsolete Save on Bitmap in this version — both overloads carry CS0618, which is why
        // every render test in this suite raises one. Suppressed in this one place instead of at each new call site,
        // so the build's warning count stays where ET-35 left it.
#pragma warning disable CS0618
        frame!.Save(path);
#pragma warning restore CS0618

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
