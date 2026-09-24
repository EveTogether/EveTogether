using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Killmails.Entities;

public sealed class LocalKillmailItemConfiguration : IEntityTypeConfiguration<LocalKillmailItem>
{
    public void Configure(EntityTypeBuilder<LocalKillmailItem> builder)
    {
        builder.HasKey(item => new { item.CharacterId, item.KillmailId, item.Flag, item.TypeId, item.IsNested });
        builder.HasOne<LocalKillmail>()
            .WithMany(killmail => killmail.Items)
            .HasForeignKey(item => new { item.CharacterId, item.KillmailId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
