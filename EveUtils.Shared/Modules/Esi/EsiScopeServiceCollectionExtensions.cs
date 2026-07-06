using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Esi;

public static class EsiScopeServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module's ESI scope catalog. Call from <c>AddXxxModule</c>, mirroring
    /// the per-module handler/permission registration pattern.
    /// </summary>
    public static IServiceCollection AddModuleEsiScopes(this IServiceCollection services, IEsiScopeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        services.AddSingleton(catalog);
        return services;
    }

    /// <summary>
    /// Builds the startup <see cref="IEsiScopeRegistry"/> from all registered <see cref="IEsiScopeCatalog"/>s.
    /// Call once per host composition root after all module registrations. The registry is used to
    /// populate the auth-URL scope parameter (client) and to declare server optional scopes (server).
    /// </summary>
    public static IServiceCollection AddEsiScopeRegistry(this IServiceCollection services)
    {
        services.AddSingleton<IEsiScopeRegistry>(sp =>
            new EsiScopeRegistry(sp.GetServices<IEsiScopeCatalog>()));
        return services;
    }
}
