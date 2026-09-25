using System;
using System.Collections.Generic;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels;

public sealed class CompositionCharacterReadiness(
    string name, CompositionReadinessStatus status, TimeSpan? toFly,
    IReadOnlyList<SkillGapViewModel> missingSkills, string queueSummary)
{
    public string Name { get; } = name;
    public CompositionReadinessStatus Status { get; } = status;
    public TimeSpan? ToFly { get; } = toFly;
    public string StatusLabel => Status switch
    {
        CompositionReadinessStatus.Ready => "✓ ready",
        CompositionReadinessStatus.Flies => "flies",
        CompositionReadinessStatus.NotYet => "not yet",
        _ => "unknown"
    };
    public string ToFlyLabel => Status == CompositionReadinessStatus.Ready
        ? "—"
        : ToFly is { } time ? EveDurationFormatter.Format(time) : "—";
    public IReadOnlyList<SkillGapViewModel> MissingSkills { get; } = missingSkills;
    public string QueueSummary { get; } = queueSummary;
}
