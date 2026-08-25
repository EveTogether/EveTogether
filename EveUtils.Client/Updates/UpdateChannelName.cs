using System;
using System.Runtime.InteropServices;

namespace EveUtils.Client.Updates;

/// <summary>
/// The update feed this build is allowed to read: <c>{platform}-{architecture}</c> — <c>win-x64</c>,
/// <c>linux-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>. These are the names <c>vpk pack --channel</c> writes.
/// </summary>
public static class UpdateChannelName
{
    // The architecture is part of the name, not a filter over it: one release carries all four RIDs, so a channel
    // named for the platform alone would offer an Apple Silicon install the x64 package.
    public static string Current => For(Platform(), RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// The name for a given platform/architecture — the seam that lets all four be asked for from one machine.
    /// </summary>
    internal static string For(string platform, Architecture architecture) =>
        $"{platform}-{_Architecture(architecture)}";

    internal static string Platform()
    {
        if (OperatingSystem.IsWindows())
            return "win";
        if (OperatingSystem.IsMacOS())
            return "osx";
        if (OperatingSystem.IsLinux())
            return "linux";

        throw new PlatformNotSupportedException(
            "EVE Together is published for Windows, macOS and Linux; this platform has no update channel.");
    }

    private static string _Architecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"EVE Together is published for x64 and arm64; {architecture} has no update channel."),
    };
}
