using System;
using System.IO;
using System.Reflection;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.App;

/// <summary>
/// Application identity shared across the client and server hosts: product name, the contact info ESI
/// requires in a User-Agent, and the running build's version. The version is read from the entry
/// assembly. Nightly builds carry a separate display identity in the informational version's build metadata;
/// dev builds fall back to the assembly default in <c>Directory.Build.props</c>.
/// </summary>
public static class AppInfo
{
    public const string Name = "EVE Together";

    /// <summary>
    /// Makers' in-game names plus the project repository so CCP — or a user — can reach the authors
    /// out-of-game. A project URL rather than a personal handle: it stays valid in the public,
    /// redistributed build that self-hosters also run.
    /// </summary>
    public const string Contact = "ign: raymondkrah, ign: Jithran, https://github.com/EveTogether/EveTogether";

    /// <summary>Running build version (e.g. "0.1.0-alpha"), without a leading "v".</summary>
    public static string Version { get; } = _ResolveVersion();

    public static string DisplayVersion { get; } = _ResolveDisplayVersion();

    /// <summary>
    /// When the entry assembly's file was written — the publish step's timestamp for a CI build, a local
    /// dev build's own compile time otherwise (ET-339). Null when the entry assembly has no path to read
    /// (e.g. a test host), so a display never fabricates a date it does not have.
    /// </summary>
    public static DateOnly? BuildDate { get; } = _ResolveBuildDate();

    /// <summary>Descriptive ESI/HTTP User-Agent tagged with the host that sent the call.</summary>
    public static string UserAgent(ExecutionHost host) => $"{Name} ({host})/{Version} ({Contact})";

    private static string _ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+'); // build metadata does not change the package version
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string _ResolveDisplayVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? $"v{Version}"
            : DisplayVersionFromInformational(informational);
    }

    internal static string DisplayVersionFromInformational(string informational)
    {
        int plus = informational.IndexOf('+');
        if (plus >= 0 && informational[(plus + 1)..].StartsWith("nightly-", StringComparison.Ordinal))
        {
            return informational[(plus + 1)..];
        }

        return $"v{(plus >= 0 ? informational[..plus] : informational)}";
    }

    private static DateOnly? _ResolveBuildDate()
    {
        var location = (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).Location;
        if (string.IsNullOrEmpty(location) || !File.Exists(location))
            return null;

        return DateOnly.FromDateTime(File.GetLastWriteTimeUtc(location));
    }
}
