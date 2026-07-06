using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Market.Entities;

public sealed class LocalMarketPriceConfiguration : IEntityTypeConfiguration<LocalMarketPrice>
{
    public void Configure(EntityTypeBuilder<LocalMarketPrice> builder)
    {
        builder.HasKey(p => p.TypeId);
        builder.Property(p => p.TypeId).ValueGeneratedNever();   // the EVE type id, not a generated key
    }
}
