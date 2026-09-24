using System;
using System.Reflection;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.App;

/// <summary>
/// Application identity shared across the client and server hosts: product name, the contact info ESI
/// requires in a User-Agent, and the running build's version. The version is read from the entry
/// assembly. Dev builds fall back to the assembly default in <c>Directory.Build.props</c>.
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

    public static string? BuildDetails { get; } = _ResolveBuildDetails();

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
        string version = plus >= 0 ? informational[..plus] : informational;
        return version.Contains("-nightly.", StringComparison.Ordinal) ? version : $"v{version}";
    }

    private static string? _ResolveBuildDetails()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = informational?.IndexOf('+') ?? -1;
        return plus >= 0 && informational is not null && informational[..plus].Contains("-nightly.", StringComparison.Ordinal)
            ? $"Build: {informational[(plus + 1)..]}"
            : null;
    }
}
