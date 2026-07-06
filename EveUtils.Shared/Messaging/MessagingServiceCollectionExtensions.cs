using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local, in-process event bus as a shared singleton. Call from the composition
    /// root of every host. The external server↔client bus is separate and arrives later as its own
    /// layer — without changing this contract.
    /// </summary>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, InProcessEventBus>();
        return services;
    }
}
