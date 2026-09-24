using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Transport;

public sealed class PendingServerRevokeConfiguration : IEntityTypeConfiguration<PendingServerRevoke>
{
    public void Configure(EntityTypeBuilder<PendingServerRevoke> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Address).HasMaxLength(512);
        builder.Property(r => r.AccessToken).HasMaxLength(512);
        builder.HasIndex(r => r.Address);
    }
}
