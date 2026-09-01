using EveUtils.Shared.Modules.Backup.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Backup;

/// <summary>
/// Server-only backup audit persistence: who downloaded a server backup archive, and when. Entity-owning,
/// so it lives in Shared but is only loaded by the server context — the table lands in the server DB.
/// The export/restore engine itself is a server-host concern (it needs the relational EF metadata, which
/// Shared does not reference) and lives in <c>EveUtils.Server/Backup</c>.
/// </summary>
public static class BackupModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new BackupDownloadConfiguration());
    }
}
