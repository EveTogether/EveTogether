using System;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The training math behind the (later) skill-queue view: the CCP SP formula, the Omega SP/min rate, the EVE
/// duration format, and folding attribute implants into the effective attributes that drive the rate.
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
    [InlineData(90, "1h 30m")]          // leading zero day/month dropped
    [InlineData(24224, "16d 19h 44m")]  // 16d19h44m
    [InlineData(50400, "1mo 5d 0h 0m")] // 35 days = 1 month + 5 days; inner zeros kept
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
    public void CharacterAttributeResolver_AddsImplantBonusesToBaseAttributes()
    {
        // Real attribute-enhancer implants carry their +stat on the "xxxBonus" attribute (178/177), NOT the bare
        // character attribute (167/166) — the resolver maps the bonus onto the matching character attribute.
        var dogma = new FakeDogmaDataAccessor()
            .Type(30000, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.PerceptionBonus, 5)) // +5 Perception implant
            .Type(30001, 0, 0, new SdeDogmaAttribute(DogmaAttributeIds.MemoryBonus, 4));     // +4 Memory implant
        var baseAttributes = new CharacterAttributes
        {
            CharacterId = 1, Charisma = 19, Intelligence = 20, Memory = 21, Perception = 22, Willpower = 23
        };

        var effective = new CharacterAttributeResolver(dogma).Resolve(baseAttributes, [30000, 30001]);

        Assert.Equal(27, effective.Perception);     // 22 + 5
        Assert.Equal(25, effective.Memory);          // 21 + 4
        Assert.Equal(20, effective.Intelligence);    // unchanged
        Assert.Equal(27, effective.For(DogmaAttributeIds.Perception)); // looked up by SDE attribute id
    }
}
