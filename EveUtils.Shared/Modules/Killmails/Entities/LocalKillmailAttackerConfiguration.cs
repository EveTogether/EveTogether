using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Killmails.Entities;

public sealed class LocalKillmailAttackerConfiguration : IEntityTypeConfiguration<LocalKillmailAttacker>
{
    public void Configure(EntityTypeBuilder<LocalKillmailAttacker> builder)
    {
        builder.HasKey(attacker => new { attacker.CharacterId, attacker.KillmailId, attacker.Ordinal });
        builder.HasOne<LocalKillmail>()
            .WithMany(killmail => killmail.Attackers)
            .HasForeignKey(attacker => new { attacker.CharacterId, attacker.KillmailId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
