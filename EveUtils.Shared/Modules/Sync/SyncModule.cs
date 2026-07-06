using EveUtils.Shared.Modules.Sync.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Sync;

/// <summary>
/// Server-only module (sync logging). The entity lives in <c>Shared</c> (migration reachability); only the
/// <c>ServerDbContext</c> applies the config. Handlers and repository are auto-registered via
/// <c>AddSharedServices</c>, so this module no longer has a DI registration method — only
/// <see cref="ConfigureModel"/>.
/// </summary>
public static class SyncModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new SyncLogConfiguration());
}
