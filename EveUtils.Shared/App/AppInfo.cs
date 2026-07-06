using System.Reflection;
using EveUtils.Shared.Runtime;

namespace EveUtils.Shared.App;

/// <summary>
/// Application identity shared across the client and server hosts: product name, the contact info ESI
/// requires in a User-Agent, and the running build's version. The version is read from the entry
/// assembly so it always reflects the tag the release pipeline injects (<c>-p:Version</c>); dev builds
/// fall back to the assembly default in <c>Directory.Build.props</c>.
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

    /// <summary>Descriptive ESI/HTTP User-Agent tagged with the host that sent the call.</summary>
    public static string UserAgent(ExecutionHost host) => $"{Name} ({host})/{Version} ({Contact})";

    private static string _ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+'); // drop the +<git-sha> source-revision suffix
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
