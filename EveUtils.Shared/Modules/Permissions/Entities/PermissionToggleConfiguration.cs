using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Permissions.Entities;

/// <summary>EF mapping for <see cref="PermissionToggle"/>. Keyed by the permission code. Server-only
/// table — table name = entity name ("PermissionToggle"), per convention (no ToTable — Shared references
/// base EF Core only).</summary>
public sealed class PermissionToggleConfiguration : IEntityTypeConfiguration<PermissionToggle>
{
    public void Configure(EntityTypeBuilder<PermissionToggle> builder)
    {
        builder.HasKey(t => t.Code);
        builder.Property(t => t.Code).HasMaxLength(128);
    }
}
