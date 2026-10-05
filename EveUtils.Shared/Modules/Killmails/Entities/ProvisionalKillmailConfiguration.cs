using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Killmails.Entities;

public sealed class ProvisionalKillmailConfiguration : IEntityTypeConfiguration<ProvisionalKillmail>
{
    public void Configure(EntityTypeBuilder<ProvisionalKillmail> builder)
    {
        builder.HasKey(killmail => killmail.Id);
        builder.Property(killmail => killmail.Id).ValueGeneratedNever();
    }
}
