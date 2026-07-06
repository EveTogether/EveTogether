using System;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Builds the SDE-backed type-name resolver, or the "type {id}" fallback when the SDE store has not been imported yet.
/// The single place that decides which resolver to use from the service provider (the resolver is not a DI service —
/// it wraps the optional <see cref="ISdeAccessor"/>), shared by the fit browser, the fit picker and the composition
/// editor.
/// </summary>
public static class FitNameResolverFactory
{
    public static ISdeNameResolver For(IServiceProvider? services)
    {
        var sde = services?.GetService<ISdeAccessor>();
        return sde is null ? FallbackNameResolver.Instance : new SdeNameResolver(sde);
    }
}
