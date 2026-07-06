using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Exercises the fit object-graph scaffolding: <see cref="DogmaItem"/> seeding/forcing/get-or-add and the
/// <see cref="DogmaValue"/> memoisation + modifier registration the passes build on.
/// </summary>
public class DogmaItemTests
{
    private static DogmaItem Item(ModuleState state = ModuleState.Online, params (int Id, double Value)[] attributes) =>
        new(587, state, groupId: 25, categoryId: 6, isAlwaysOn: false,
            attributes.Select(a => new SdeDogmaAttribute(a.Id, a.Value)));

    [Fact]
    public void Constructor_SeedsBaseAttributes()
    {
        var item = Item(ModuleState.Active, (64, 1.5), (9, 400));

        Assert.True(item.TryGet(64, out var damage));
        Assert.Equal(1.5, damage.BaseValue);
        Assert.False(damage.IsForced);
        Assert.Null(damage.Resolved);
        Assert.Equal(ModuleState.Active, item.State);
    }

    [Fact]
    public void GetOrAdd_ReturnsExisting_ForCarriedAttribute()
    {
        var item = Item(attributes: (64, 1.5));

        var value = item.GetOrAdd(64, defaultValue: 1.0);

        Assert.Equal(1.5, value.BaseValue);   // the carried value, not the default
    }

    [Fact]
    public void GetOrAdd_CreatesFromDefault_ForMissingAttribute()
    {
        var item = Item();

        var value = item.GetOrAdd(263, defaultValue: 0.0);

        Assert.Equal(0.0, value.BaseValue);
        Assert.True(item.TryGet(263, out _));   // now present on the item
    }

    [Fact]
    public void Force_SetsForcedValue_BypassingModifiers()
    {
        var item = Item(attributes: (280, 0));

        item.Force(280, 5);   // skill level V

        Assert.True(item.TryGet(280, out var level));
        Assert.Equal(5, level.BaseValue);
        Assert.True(level.IsForced);
    }

    [Fact]
    public void DogmaValue_MemoisesResolved_AndCollectsModifiers()
    {
        var source = Item(attributes: (64, 1.1));
        var value = new DogmaValue(1.0);

        Assert.Null(value.Resolved);
        value.AddModifier(new Modifier(EffectOperator.PostMul, source, SourceAttributeId: 64, Penalize: true));
        value.Resolved = 1.1;

        Assert.Single(value.Modifiers);
        Assert.Equal(64, value.Modifiers[0].SourceAttributeId);
        Assert.Same(source, value.Modifiers[0].Source);
        Assert.True(value.Modifiers[0].Penalize);
        Assert.Equal(1.1, value.Resolved);
    }
}
