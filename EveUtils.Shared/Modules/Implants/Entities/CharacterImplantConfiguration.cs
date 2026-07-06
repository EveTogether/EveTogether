using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EveUtils.Shared.Modules.Implants.Entities;

public sealed class CharacterImplantConfiguration : IEntityTypeConfiguration<CharacterImplant>
{
    public void Configure(EntityTypeBuilder<CharacterImplant> builder)
    {
        builder.HasKey(i => new { i.CharacterId, i.ImplantTypeId });
        builder.Property(i => i.CharacterId).ValueGeneratedNever();
        builder.Property(i => i.ImplantTypeId).ValueGeneratedNever();
    }
}
