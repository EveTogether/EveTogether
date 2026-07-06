using EveUtils.Shared.Modules.AdminAuth;
using EveUtils.Shared.Modules.Fittings;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Gamelog;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Permissions;
using EveUtils.Shared.Modules.ServerAuth;
using EveUtils.Shared.Modules.Sync;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Data;

/// <summary>Server context (Docker): shared modules + server-only modules. Multi-provider.</summary>
public sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : SharedDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);                  // shared modules (Ships)
        GamelogModule.ConfigureModel(modelBuilder);          // gamelog samples (server receives replays)
        SyncModule.ConfigureModel(modelBuilder);             // server-only
        ServerAuthModule.ConfigureModel(modelBuilder);       // server-only: pairing/sessions/allowed-list
        AdminAuthModule.ConfigureModel(modelBuilder);        // server-only: admin users/roles/RBAC
        FittingsModule.ConfigureServerModel(modelBuilder);   // server-only: SharedFit store
        PermissionsModule.ConfigureModel(modelBuilder);      // server-only: app-permission toggles
        FleetModule.ConfigureModel(modelBuilder);            // server-only: fleets/wings/squads/members/invites
        MessagingModule.ConfigureModel(modelBuilder);        // server-only: internal mail/invite queue
    }
}
