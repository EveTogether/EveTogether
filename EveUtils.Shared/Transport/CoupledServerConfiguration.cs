using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Transport;

/// <summary>EF mapping for <see cref="CoupledServer"/>. Client-only table — applied by the
/// ClientDbContext. Table name = entity name ("CoupledServer"), per convention (no ToTable — Shared
/// references base EF Core only).</summary>
public sealed class CoupledServerConfiguration : IEntityTypeConfiguration<CoupledServer>
{
    public void Configure(EntityTypeBuilder<CoupledServer> builder)
    {
        builder.HasKey(s => s.Address);
        builder.Property(s => s.Address).HasMaxLength(512);     // raw server URL
        builder.Property(s => s.Label).HasMaxLength(255);
        builder.Property(s => s.ServerName).HasMaxLength(255);
        builder.Property(s => s.CertFingerprint).HasMaxLength(128); // SHA-256 hex = 64 chars
    }
}
