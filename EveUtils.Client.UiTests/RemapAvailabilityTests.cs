using System;
using EveUtils.Shared.Modules.Skills;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-354 A4: the OPTIMISE tab's "next remap" line never invents a date when ESI has not reported the
/// cooldown, and a banked bonus remap always reads as available now regardless of the cooldown date.</summary>
public class RemapAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Criterion 4. Red if a missing cooldown falls through to a guessed date instead of "unknown".</summary>
    [Theory]
    [InlineData(null, 0, "unknown · bonus remaps 0")]
    [InlineData(-1, 0, "available now · bonus remaps 0")]
    [InlineData(30, 0, "on ")]
    [InlineData(30, 2, "available now · bonus remaps 2")]
    public void Describe_ReadsCooldownAndBonusRemaps(int? cooldownOffsetDays, int bonusRemaps, string expectedFragment)
    {
        DateTimeOffset? cooldown = cooldownOffsetDays is { } days ? Now.AddDays(days) : null;

        var text = RemapAvailability.Describe(cooldown, bonusRemaps, Now);

        Assert.Contains(expectedFragment, text);
    }
}
