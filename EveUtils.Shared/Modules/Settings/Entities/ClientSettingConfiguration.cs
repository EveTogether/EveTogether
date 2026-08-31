using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Settings.Entities;

public sealed class ClientSettingConfiguration : IEntityTypeConfiguration<ClientSetting>
{
    public void Configure(EntityTypeBuilder<ClientSetting> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).HasMaxLength(100);

        // 4000: comfortably beyond FleetStructureLimits.MaxFleetSize (256) even at the widest character id
        // (10 digits + a separator = 11 chars/id, 2816 for a full fleet) — a margin, not a tight fit, so the next
        // comma-separated ui.* value does not reopen this same migration.
        builder.Property(s => s.Value).HasMaxLength(4000);
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
