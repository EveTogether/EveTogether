using System.Reflection;
using EveUtils.Shared.Cqrs;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.DependencyInjection;

/// <summary>
/// Central, Scrutor-driven auto-registration. A single scan per assembly registers, against their
/// implemented interfaces: CQRS handlers (<see cref="ICommandHandler{T}"/>/<see cref="ICommandHandler{T,R}"/>/
/// <see cref="IQueryHandler{T,R}"/>), repositories (<see cref="IRepository"/>) and lifetime-marked services
/// (<see cref="IScopedService"/>/<see cref="ITransientService"/>/<see cref="ISingletonService"/>). No
/// hand-written AddScoped lists, and a shared service is defined once via its marker — never registered twice
/// per host.
///
/// Host differences are expressed at runtime via <c>IRuntimeContext.Host</c> (ExecutionHost), not by splitting
/// code across namespaces (anti-splintering). Genuinely host-only types may instead live in the client
/// or server project and be auto-registered there by calling <see cref="AddAutoServices"/> on that assembly.
/// </summary>
public static class ModuleRegistrationExtensions
{
    /// <summary>Auto-registers handlers, repositories and lifetime-marked services found in the shared assembly.
    /// Both hosts call this once.</summary>
    public static IServiceCollection AddSharedServices(this IServiceCollection services) =>
        services.AddAutoServices(typeof(IScopedService).Assembly);

    /// <summary>Auto-registers handlers, repositories and lifetime-marked services found in <paramref name="assembly"/>.
    /// Reusable for a host's own assembly so host-only types can live in the client/server project.</summary>
    public static IServiceCollection AddAutoServices(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(scan => scan.FromAssemblies(assembly)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            // A repository is Scoped by default; one that carries an explicit lifetime marker
            // (e.g. ISingletonService) is registered by the marker scan below instead, so it is never registered twice.
            .AddClasses(c => c.Where(t => (typeof(IRepository).IsAssignableFrom(t)
                                          || t.Name.EndsWith("Repository", StringComparison.Ordinal))
                                          && !typeof(IScopedService).IsAssignableFrom(t)
                                          && !typeof(ITransientService).IsAssignableFrom(t)
                                          && !typeof(ISingletonService).IsAssignableFrom(t)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IScopedService>(), publicOnly: false)
                .AsSelfWithInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ITransientService>(), publicOnly: false)
                .AsSelfWithInterfaces().WithTransientLifetime()
            .AddClasses(c => c.AssignableTo<ISingletonService>(), publicOnly: false)
                .AsSelfWithInterfaces().WithSingletonLifetime());

        return services;
    }
}
