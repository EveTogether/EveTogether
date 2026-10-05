using System;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The training math behind the (later) skill-queue view: the CCP SP formula, the Omega SP/min rate, the EVE
/// duration format, and the effective ESI attributes that drive the rate.
/// </summary>
public class SkillTrainingMathTests
{
    [Theory]
    [InlineData(1, 0, 0)]        // untrained
    [InlineData(1, 1, 250)]      // rank-1, level 1
    [InlineData(1, 5, 256000)]   // rank-1, level 5 = 250 * 32^2
    [InlineData(3, 5, 768000)]   // rank scales linearly
    public void SkillPointsForLevel_MatchesCcpFormula(int rank, int level, double expected) =>
        Assert.Equal(expected, SkillPointMath.SkillPointsForLevel(rank, level), 2);

    [Theory]
    [InlineData(20, 20, 30)]     // primary + secondary/2
    [InlineData(27, 21, 37.5)]   // with +stat implants on the attributes
    public void SkillPointsPerMinute_IsPrimaryPlusHalfSecondary(double primary, double secondary, double expected) =>
        Assert.Equal(expected, SkillPointMath.SkillPointsPerMinute(primary, secondary), 2);

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(53, "53m")]             // under an hour: minutes only
    [InlineData(90, "1h 30m")]          // under a day: hours and minutes
    [InlineData(24224, "16d 19h")]      // a day or more: days and hours, never months
    [InlineData(363780, "252d 15h")]    // a long queue stays in days, as the queue tab writes it
    public void EveDurationFormatter_FormatsTheEveWay(int totalMinutes, string expected) =>
        Assert.Equal(expected, EveDurationFormatter.Format(TimeSpan.FromMinutes(totalMinutes)));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(509, "8m 29s")]      // the capacitor readout: 509s -> 8m 29s
    [InlineData(3785, "1h 3m 5s")]   // inner zero-minutes kept once hours show
    public void EveDurationFormatter_FormatWithSeconds_DropsLeadingZeroUnits(int totalSeconds, string expected) =>
        Assert.Equal(expected, EveDurationFormatter.FormatWithSeconds(TimeSpan.FromSeconds(totalSeconds)));

    [Fact]
    public void CharacterAttributeResolver_EsiAttributesWithImplants_UsesEffectiveRate()
    {
        var (resolver, esiAttributes, implants) = _PlusFourCharacter();
        var effective = resolver.Resolve(esiAttributes, implants);

        Assert.Equal(new CharacterAttributeSet(21, 21, 31, 25, 21), effective);
        Assert.Equal(35.5, SkillPointMath.SkillPointsPerMinute(
            effective.For(DogmaAttributeIds.Perception), effective.For(DogmaAttributeIds.Willpower)));
    }

    [Fact]
    public void CharacterAttributeResolver_Base_RemovesSdeImplantBonuses()
    {
        var (resolver, esiAttributes, implants) = _PlusFourCharacter();
        var baseAttributes = resolver.Base(esiAttributes, implants);

        Assert.Equal(new CharacterAttributeSet(17, 17, 27, 21, 17), baseAttributes);
        Assert.Equal(99, baseAttributes.Charisma + baseAttributes.Intelligence + baseAttributes.Memory
            + baseAttributes.Perception + baseAttributes.Willpower);
    }

    private static (CharacterAttributeResolver Resolver, CharacterAttributes Attributes, int[] Implants) _PlusFourCharacter()
    {
        var dogma = new FakeDogmaDataAccessor()
            .Type(30000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.CharismaBonus, 4))
            .Type(30001, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.IntelligenceBonus, 4))
            .Type(30002, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.MemoryBonus, 4))
            .Type(30003, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.PerceptionBonus, 4))
            .Type(30004, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.WillpowerBonus, 4));
        var esiAttributes = new CharacterAttributes
        {
            CharacterId = 1, Charisma = 21, Intelligence = 21, Memory = 31, Perception = 25, Willpower = 21
        };
        return (new CharacterAttributeResolver(dogma), esiAttributes, [30000, 30001, 30002, 30003, 30004]);
    }
}
