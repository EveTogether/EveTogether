using System;
using EveUtils.Shared.DependencyInjection;
using Velopack;
using Velopack.Locators;

namespace EveUtils.Client.Updates;

internal sealed class VelopackUpdateSupportProbe : IUpdateSupportProbe, ISingletonService
{
    public UpdateSupport Detect() => Detect(IsInstalledCopy);

    // A host that never ran the bootstrap in Main — the test host, the designer — has no locator at all, and
    // VelopackLocator.Current throws when unset. Asked rather than caught: this is an ordinary state.
    internal static bool IsInstalledCopy() =>
        IsInstalledCopy(VelopackLocator.IsCurrentSet, static () => VelopackLocator.Current.CurrentlyInstalledVersion);

    /// <summary>
    /// The rule with both readings handed in — Velopack's locator is a process-wide singleton a test cannot stand up.
    /// </summary>
    internal static bool IsInstalledCopy(bool locatorIsSet, Func<SemanticVersion?> installedVersion) =>
        locatorIsSet && installedVersion() is not null;

    // Establishing the answer reads the installation on disk; anything that cannot be established is NotInstalled,
    // the answer that offers less rather than more.
    internal static UpdateSupport Detect(Func<bool> isInstalled)
    {
        try
        {
            return isInstalled() ? UpdateSupport.Supported : UpdateSupport.NotInstalled;
        }
        catch (Exception)
        {
            return UpdateSupport.NotInstalled;
        }
    }
}
