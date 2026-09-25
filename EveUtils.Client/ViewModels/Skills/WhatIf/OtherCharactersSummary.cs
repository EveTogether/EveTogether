using System;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// ET-358 AC3: the "your other characters" section of the WHAT IF panel — always exactly the two lines below,
/// never one row per character. <see cref="FlyItTodayLine"/> and <see cref="SoonestLine"/> are the whole of it; there
/// is nowhere else for a character to add a row.
/// </summary>
public sealed record OtherCharactersSummary(int FlyItTodayCount, TimeSpan? SoonestTimeLeft, DateTimeOffset? SoonestDate)
{
    public string FlyItTodayLine => $"fly it today: {FlyItTodayCount}";

    public string SoonestLine => SoonestDate is null
        ? "soonest of the rest: —"
        : $"soonest of the rest: {Home.SkillQueueStanding.Until(SoonestTimeLeft ?? TimeSpan.Zero)} · {SoonestDate:ddd d MMM}";
}
