using System.Reflection;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Runtime;
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
///
/// A shared type that one host cannot even <i>construct</i> is the exception, and it is a composition-time
/// question rather than a runtime one: the scan takes the <see cref="ExecutionHost"/> it is registering for and
/// skips what carries <see cref="ClientOnlyAttribute"/> on the server. Without that the server registered the
/// Runs handlers — every one of them asks for the client database — and fell over at startup on a dependency
/// it can never supply (ET-180).
/// </summary>
public static class ModuleRegistrationExtensions
{
    /// <summary>Auto-registers handlers, repositories and lifetime-marked services found in the shared assembly.
    /// Both hosts call this once, each naming the host it is composing for.</summary>
    public static IServiceCollection AddSharedServices(this IServiceCollection services, ExecutionHost host) =>
        services.AddAutoServices(typeof(IScopedService).Assembly, host);

    /// <summary>Auto-registers handlers, repositories and lifetime-marked services found in <paramref name="assembly"/>.
    /// Reusable for a host's own assembly so host-only types can live in the client/server project.</summary>
    public static IServiceCollection AddAutoServices(this IServiceCollection services, Assembly assembly, ExecutionHost host)
    {
        // Composable on this host at all? Filters accumulate (AND), so this rides along with each scan below
        // rather than being repeated as a condition inside them.
        bool Composable(Type type) =>
            host == ExecutionHost.Client || !type.IsDefined(typeof(ClientOnlyAttribute), inherit: false);

        services.Scan(scan => scan.FromAssemblies(assembly)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<>)).Where(Composable), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)).Where(Composable), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)).Where(Composable), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            // A repository is Scoped by default; one that carries an explicit lifetime marker
            // (e.g. ISingletonService) is registered by the marker scan below instead, so it is never registered twice.
            .AddClasses(c => c.Where(t => (typeof(IRepository).IsAssignableFrom(t)
                                          || t.Name.EndsWith("Repository", StringComparison.Ordinal))
                                          && !typeof(IScopedService).IsAssignableFrom(t)
                                          && !typeof(ITransientService).IsAssignableFrom(t)
                                          && !typeof(ISingletonService).IsAssignableFrom(t)).Where(Composable), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<IScopedService>().Where(Composable), publicOnly: false)
                .AsSelfWithInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo<ITransientService>().Where(Composable), publicOnly: false)
                .AsSelfWithInterfaces().WithTransientLifetime()
            .AddClasses(c => c.AssignableTo<ISingletonService>().Where(Composable), publicOnly: false)
                .AsSelfWithInterfaces().WithSingletonLifetime());

        return services;
    }
}
