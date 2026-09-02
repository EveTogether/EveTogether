using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.ApiKeys.Entities;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Label).HasMaxLength(255);
        builder.Property(k => k.Prefix).HasMaxLength(16);
        builder.Property(k => k.SecretHash).HasMaxLength(64);
        builder.Property(k => k.Scopes).HasMaxLength(512);
        builder.Property(k => k.CreatedBy).HasMaxLength(255);
        builder.HasIndex(k => k.Prefix).IsUnique();
    }
}
