using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>EF mapping for <see cref="Fleet"/>. Table name = entity name.</summary>
public sealed class FleetConfiguration : IEntityTypeConfiguration<Fleet>
{
    /// <summary>
    /// When <c>false</c> (the server default) <see cref="Fleet.IsClientOnly"/> is ignored, so the server table
    /// never gains that column — the flag is purely a client-only concept. The client passes
    /// <c>true</c> to materialise the column in its local SQLite, keeping both hosts on the one Shared entity.
    /// </summary>
    private readonly bool _mapClientOnlyFlag;

    public FleetConfiguration(bool mapClientOnlyFlag = false) => _mapClientOnlyFlag = mapClientOnlyFlag;

    public void Configure(EntityTypeBuilder<Fleet> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(255);
        builder.Property(f => f.Description).HasMaxLength(2000);
        builder.Property(f => f.Motd).HasMaxLength(2000);

        if (_mapClientOnlyFlag)
            builder.Property(f => f.IsClientOnly);
        else
            builder.Ignore(f => f.IsClientOnly); // server-irrelevant: no column in the server DB.

        // Discovery of a creator's fleets and the open-fleet list query these.
        builder.HasIndex(f => f.CreatorCharacterId);
        builder.HasIndex(f => new { f.Visibility, f.State });
    }
}
