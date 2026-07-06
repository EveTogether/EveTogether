using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Gamelog;
using EveUtils.Shared.Modules.Market;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Settings;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Implants;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Data;

/// <summary>Desktop-client context: shared modules + client-only modules. Draait op SQLite.</summary>
public sealed class ClientDbContext(DbContextOptions<ClientDbContext> options) : SharedDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);                    // shared modules (Ships)
        GamelogModule.ConfigureModel(modelBuilder);            // gamelog samples (client writes locally)
        SettingsModule.ConfigureModel(modelBuilder);           // client-only settings
        MarketModule.ConfigureModel(modelBuilder);             // client-only cached ESI market prices
        SkillsModule.ConfigureModel(modelBuilder);             // client-only imported character skills
        ImplantsModule.ConfigureModel(modelBuilder);           // client-only imported character implants
        FittingsModule.ConfigureClientModel(modelBuilder);     // client-local fittings
        FleetModule.ConfigureClientModel(modelBuilder);        // client-only fleets: same Shared entities + IsClientOnly
        modelBuilder.ApplyConfiguration(new LocalCharacterConfiguration()); // client-local character registry
        modelBuilder.ApplyConfiguration(new CoupledServerConfiguration());      // client-local coupled servers + trust
        modelBuilder.ApplyConfiguration(new ClientServerSessionConfiguration()); // client-local server sessions
        modelBuilder.ApplyConfiguration(new ClientInboxMessageConfiguration());  // client-local message inbox
        modelBuilder.ApplyConfiguration(new CachedExternalCharacterConfiguration()); // client-local external-character cache
    }
}
