using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Settings.Entities;

public sealed class ClientSettingConfiguration : IEntityTypeConfiguration<ClientSetting>
{
    public void Configure(EntityTypeBuilder<ClientSetting> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(512);
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
