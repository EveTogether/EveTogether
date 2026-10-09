using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLossPriceConfiguration : IEntityTypeConfiguration<RunLossPrice>
{
    public void Configure(EntityTypeBuilder<RunLossPrice> builder)
    {
        builder.HasKey(price => new { price.RunId, price.CharacterId, price.KillmailId, price.TypeId });
        builder.Property(price => price.UnitPriceIsk).HasPrecision(18, 2);
        builder.HasOne<Run>()
            .WithMany()
            .HasForeignKey(price => price.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
