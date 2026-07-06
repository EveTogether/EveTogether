using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Cqrs.Permissions;

public static class PermissionServiceCollectionExtensions
{
    /// <summary>
    /// Registers a module's permission catalog. Call from <c>AddXxxModule</c>, mirroring the
    /// per-module handler registration.
    /// </summary>
    public static IServiceCollection AddModulePermissions(this IServiceCollection services, IPermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        services.AddSingleton(catalog);
        return services;
    }

    /// <summary>
    /// Builds the code-derived <see cref="IPermissionRegistry"/> from all registered catalogs and
    /// wires the v1 <see cref="OwnerAllPolicy"/>. Call once from each host's composition root, after
    /// the modules are registered (resolution is order-independent — catalogs are collected lazily).
    /// </summary>
    public static IServiceCollection AddPermissionRegistry(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionRegistry>(sp => new PermissionRegistry(sp.GetServices<IPermissionCatalog>()));
        services.AddSingleton<IAccessPolicy, OwnerAllPolicy>();
        return services;
    }
}
