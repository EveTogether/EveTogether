using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Killmails.Entities;

public sealed class LocalKillmailConfiguration : IEntityTypeConfiguration<LocalKillmail>
{
    public void Configure(EntityTypeBuilder<LocalKillmail> builder)
    {
        builder.HasKey(killmail => new { killmail.CharacterId, killmail.KillmailId });
        builder.Property(killmail => killmail.CharacterId).ValueGeneratedNever();
        builder.Property(killmail => killmail.KillmailId).ValueGeneratedNever();
        builder.HasOne<Run>()
            .WithMany()
            .HasForeignKey(killmail => killmail.RunId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
