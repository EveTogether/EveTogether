using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Killmails.Entities;

public sealed class KillmailEntityNameConfiguration : IEntityTypeConfiguration<KillmailEntityName>
{
    public void Configure(EntityTypeBuilder<KillmailEntityName> builder)
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
    }
}
