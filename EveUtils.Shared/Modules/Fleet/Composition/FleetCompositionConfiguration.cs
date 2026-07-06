using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>EF mapping for <see cref="FleetComposition"/>. Table name = entity name.</summary>
public sealed class FleetCompositionConfiguration : IEntityTypeConfiguration<FleetComposition>
{
    /// <summary>
    /// When <c>false</c> (the server default) <see cref="FleetComposition.IsClientOnly"/> is ignored, so the server
    /// table never gains that column — it is a client-only concept. The client passes <c>true</c> to materialise the
    /// column in its local SQLite, keeping both hosts on the one Shared entity (mirrors <c>FleetConfiguration</c>).
    /// </summary>
    private readonly bool _mapClientOnlyFlag;

    public FleetCompositionConfiguration(bool mapClientOnlyFlag = false) => _mapClientOnlyFlag = mapClientOnlyFlag;

    public void Configure(EntityTypeBuilder<FleetComposition> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).IsRequired().HasMaxLength(255);
        builder.Property(c => c.Description).HasMaxLength(2000);

        if (_mapClientOnlyFlag)
            builder.Property(c => c.IsClientOnly);
        else
            builder.Ignore(c => c.IsClientOnly); // server-irrelevant: no column in the server DB.

        // An owner browses their own compositions.
        builder.HasIndex(c => c.OwnerCharacterId);
    }
}
