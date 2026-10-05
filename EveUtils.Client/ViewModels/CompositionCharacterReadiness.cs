using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels;

/// <summary>One character against one doctrine fit: <see cref="ToFly"/> is the training to fly the fit,
/// <see cref="ToMin"/> the training to the fit plus the doctrine skill minimum (equal to it without one).</summary>
public sealed class CompositionCharacterReadiness(
    string name, CompositionReadinessStatus status, TimeSpan? toFly, TimeSpan? toMin,
    IReadOnlyList<SkillGapViewModel> missingSkills, string queueSummary)
{
    private const int MissingRowsShown = 6;

    public string Name { get; } = name;
    public CompositionReadinessStatus Status { get; } = status;
    public TimeSpan? ToFly { get; } = toFly;
    public TimeSpan? ToMin { get; } = toMin;
    public int CharacterId { get; init; }

    /// <summary>The fit carries a doctrine skill minimum, so <see cref="ToMinLabel"/> is worth showing.</summary>
    public bool HasSkillMinimums { get; init; }
    public string StatusLabel => Status switch
    {
        CompositionReadinessStatus.Ready => "✓ ready",
        CompositionReadinessStatus.Flies => "flies",
        CompositionReadinessStatus.NotYet => "not yet",
        _ => "unknown"
    };
    public bool IsReady => Status == CompositionReadinessStatus.Ready;
    public bool IsFlies => Status == CompositionReadinessStatus.Flies;
    public bool IsNotYet => Status is CompositionReadinessStatus.NotYet or CompositionReadinessStatus.Unknown;
    public string ToFlyLabel => Status is CompositionReadinessStatus.Ready or CompositionReadinessStatus.Flies
        ? "—"
        : ToFly is { } time ? EveDurationFormatter.Format(time) : "—";
    public string ToMinLabel => Status == CompositionReadinessStatus.Ready
        ? "—"
        : ToMin is { } time ? EveDurationFormatter.Format(time) : "—";
    public IReadOnlyList<SkillGapViewModel> MissingSkills { get; } = missingSkills;
    public string QueueSummary { get; } = queueSummary;

    /// <summary>Skill levels still to train to fly the fit, and to reach the doctrine minimum on top of it.</summary>
    public int FlyLevels { get; init; }
    public int MinLevels { get; init; }

    /// <summary>Every level still missing for the doctrine minimum, one row per level, in training order.</summary>
    public IReadOnlyList<CompositionMissingLevel> MissingLevels { get; init; } = [];

    /// <summary>The what-if scenarios over <see cref="MissingLevels"/> as a plan (ET-358's own calculation), computed
    /// when the pane first asks — only the selected character pays for it. Null without attributes or dogma.</summary>
    public Lazy<IReadOnlyList<WhatIfScenario>>? Scenarios { get; init; }

    public string SelectedLabel => $"{Name.ToUpperInvariant()} · SELECTED";
    public string FlyHowLong => Status == CompositionReadinessStatus.Unknown ? "—"
        : FlyLevels == 0 ? "flies today" : $"{_LevelsText(FlyLevels)} · {_Time(ToFly)}";
    public string MinHowLong => Status == CompositionReadinessStatus.Unknown ? "—"
        : MinLevels == 0 ? "met today" : $"{_LevelsText(MinLevels)} · {_Time(ToMin)}";

    private WhatIfScenario? _QueueStands => MinLevels > 0 && Scenarios?.Value is { Count: > 1 } list ? list[0] : null;
    private WhatIfScenario? _PlanFirst => MinLevels > 0 && Scenarios?.Value is { Count: > 1 } list ? list[1] : null;
    public string TrainedFirstLabel => _PlanFirst is { } scenario ? _Date(scenario.Date) : "—";
    public string QueueStandsLabel => _QueueStands is { } scenario ? _Date(scenario.Date) : "—";

    public bool HasHowLongNote => _PlanFirst is not null;

    /// <summary>What the queue already holds of the missing levels, and what training them first saves.</summary>
    public string HowLongNote
    {
        get
        {
            if (_PlanFirst is not { } planFirst)
            {
                return "";
            }
            List<int> positions = [.. MissingLevels.Where(level => level.QueuePosition is not null)
                .Select(level => level.QueuePosition ?? 0).Order()];
            string queued = positions.Count == 0
                ? $"None of the {MinLevels} levels are in the queue yet."
                : $"{positions.Count} of the {MinLevels} levels are already in the queue, at " +
                  (positions[0] == positions[^1] ? $"#{positions[0]}." : $"#{positions[0]}–{positions[^1]}.");
            string saves = planFirst.TimeSaved >= TimeSpan.FromMinutes(1)
                ? $" Putting the plan first saves {EveDurationFormatter.Format(planFirst.TimeSaved)}."
                : " Putting the plan first saves no time.";
            return queued + saves;
        }
    }

    public IReadOnlyList<CompositionMissingLevel> MissingLevelsShown => [.. MissingLevels.Take(MissingRowsShown)];
    public bool HasMissingLevels => MissingLevels.Count > 0;
    public bool HasMoreMissingLevels => MissingLevels.Count > MissingRowsShown;
    public string MoreMissingLevelsLabel => $"+ {MissingLevels.Count - MissingRowsShown} more levels";

    private static string _LevelsText(int levels) => levels == 1 ? "1 level" : $"{levels} levels";
    private static string _Time(TimeSpan? time) => time is { } value ? EveDurationFormatter.Format(value) : "—";

    private static string _Date(DateTimeOffset date) => Skills.SkillsQueueViewModel.When(date, DateTimeOffset.UtcNow);
}

/// <summary>One missing skill level in the readiness pane's MISSING table; <see cref="QueuePosition"/> is the 1-based
/// place in the character's skill queue, null when the level is not queued.</summary>
public sealed record CompositionMissingLevel(int SkillTypeId, int Level, string SkillName, TimeSpan? Time, int? QueuePosition)
{
    public string SkillLabel => $"{SkillName} {RomanLevel.Text(Level)}";
    public string TimeLabel => Time is { } time ? EveDurationFormatter.Format(time) : "—";
    public string QueueLabel => QueuePosition is { } position ? $"#{position}" : "not queued";
    public bool IsQueued => QueuePosition is not null;
}
