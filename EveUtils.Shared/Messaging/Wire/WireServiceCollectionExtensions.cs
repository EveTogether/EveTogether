using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Messaging.Wire;

public static class WireServiceCollectionExtensions
{
    /// <summary>Builds the <see cref="IEventTypeRegistry"/> from every registered module catalog.</summary>
    public static IServiceCollection AddWireEvents(this IServiceCollection services)
    {
        services.AddSingleton<IEventTypeRegistry>(sp =>
        {
            var registry = new EventTypeRegistry();
            foreach (var catalog in sp.GetServices<IWireEventCatalog>())
                catalog.RegisterInto(registry);
            return registry;
        });
        return services;
    }
}
