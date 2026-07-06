using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Messaging.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Messaging;

/// <summary>
/// Registration of the Messaging module: the internal mail/invite queue. The entity lives in Shared
/// but its table lands in the server DB via <see cref="ConfigureModel"/>. Handlers + repository are
/// auto-registered by AddSharedServices; <see cref="AddMessagingModule"/> adds the wire catalog.
/// </summary>
public static class MessagingModule
{
    /// <summary>Applies the queued-message entity to the server DbContext.</summary>
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new QueuedMessageConfiguration());
    }

    /// <summary>Server composition: registers the wire catalog so deliveries travel over the remote bus.
    /// The per-kind <see cref="IMessageResponder"/>s are contributed by the feature modules that own them
    /// .</summary>
    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        services.AddSingleton<IWireEventCatalog, MessagingWireEvents>();
        return services;
    }
}
