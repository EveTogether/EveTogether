using System;
using System.Runtime.InteropServices;

namespace EveUtils.Client.Updates;

/// <summary>
/// The update feed this build is allowed to read: <c>{platform}-{architecture}-{stream}</c> — <c>win-x64-stable</c>,
/// <c>linux-x64-nightly</c>, <c>osx-arm64-stable</c>, <c>osx-x64-nightly</c>, and so on. These are the names
/// <c>vpk pack --channel</c> writes (ET-339).
/// </summary>
public static class UpdateChannelName
{
    // The architecture is part of the name, not a filter over it: one release carries all four RIDs, so a channel
    // named for the platform alone would offer an Apple Silicon install the x64 package. The stream is part of it
    // too, so a stable install can never see a nightly asset even if it asked for one by mistake.
    public static string For(UpdateChannel stream) => For(Platform(), RuntimeInformation.ProcessArchitecture, stream);

    /// <summary>
    /// The name for a given platform/architecture/stream — the seam that lets all eight be asked for from one machine.
    /// </summary>
    internal static string For(string platform, Architecture architecture, UpdateChannel stream) =>
        $"{platform}-{_Architecture(architecture)}-{(stream == UpdateChannel.Nightly ? "nightly" : "stable")}";

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
