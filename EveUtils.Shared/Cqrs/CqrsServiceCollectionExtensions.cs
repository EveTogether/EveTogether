using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Cqrs;

public static class CqrsServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="IDispatcher"/> behind the permission gate
    /// (<see cref="PermissionGateDispatcher"/>). Command/query handlers are auto-registered by
    /// <c>AddSharedServices</c>. Requires <c>AddPermissionRegistry</c> + an
    /// <see cref="IPrincipalAccessor"/> (added by the host's composition root).</summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services)
    {
        services.AddScoped<Dispatcher>();
        services.AddScoped<IDispatcher>(sp => new PermissionGateDispatcher(
            sp.GetRequiredService<Dispatcher>(),
            sp.GetRequiredService<IAccessPolicy>(),
            sp.GetRequiredService<IPrincipalAccessor>()));
        return services;
    }
}
