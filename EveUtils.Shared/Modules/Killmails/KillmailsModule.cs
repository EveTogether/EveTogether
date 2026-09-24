using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>
/// Client-only module: a character's imported kills and losses with their items and attackers. The entities live in
/// <c>Shared</c> so the migration plumbing can reach the EF model; only the <c>ClientDbContext</c> applies this config.
/// </summary>
public static class KillmailsModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LocalKillmailConfiguration());
        modelBuilder.ApplyConfiguration(new LocalKillmailItemConfiguration());
        modelBuilder.ApplyConfiguration(new LocalKillmailAttackerConfiguration());
        modelBuilder.ApplyConfiguration(new KillmailEntityNameConfiguration());
    }
}
