using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>Client identity: the single local owner, mutable once a character signs in.</summary>
    public static IServiceCollection AddLocalIdentity(this IServiceCollection services)
    {
        services.AddSingleton<LocalOwnerPrincipalAccessor>();
        services.AddSingleton<IPrincipalAccessor>(sp => sp.GetRequiredService<LocalOwnerPrincipalAccessor>());
        services.AddSingleton<ILocalPrincipalController>(sp => sp.GetRequiredService<LocalOwnerPrincipalAccessor>());
        return services;
    }

    /// <summary>Server identity (POC): a fixed "server" principal for the host's internal actions.</summary>
    public static IServiceCollection AddServerIdentity(this IServiceCollection services)
    {
        services.AddSingleton<IPrincipalAccessor>(new StaticPrincipalAccessor(new Principal("server", null)));
        return services;
    }
}
