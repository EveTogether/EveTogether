using System;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// One what-if scenario's result (ET-358): the date the plan is fully trained under that scenario, the time saved
/// against scenario 1 ("as the queue stands"), and — for the three remap-based scenarios — the base-attribute split
/// that gets there. <see cref="TimeSaved"/> is non-negative by construction: each later scenario only adds an
/// unlock (a remap, then a matched implant set) on top of the previous one, never a slower path.
/// </summary>
public sealed record WhatIfScenario(string Name, DateTimeOffset Date, TimeSpan TimeSaved, CharacterAttributeSet? RemapSplit = null);
