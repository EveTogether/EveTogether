using System;
using System.Collections.Generic;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels;

/// <summary>One character against one doctrine fit: <see cref="ToFly"/> is the training to fly the fit,
/// <see cref="ToMin"/> the training to the fit plus the doctrine skill minimum (equal to it without one).</summary>
public sealed class CompositionCharacterReadiness(
    string name, CompositionReadinessStatus status, TimeSpan? toFly, TimeSpan? toMin,
    IReadOnlyList<SkillGapViewModel> missingSkills, string queueSummary)
{
    public string Name { get; } = name;
    public CompositionReadinessStatus Status { get; } = status;
    public TimeSpan? ToFly { get; } = toFly;
    public TimeSpan? ToMin { get; } = toMin;

    /// <summary>The fit carries a doctrine skill minimum, so <see cref="ToMinLabel"/> is worth showing.</summary>
    public bool HasSkillMinimums { get; init; }
    public string StatusLabel => Status switch
    {
        CompositionReadinessStatus.Ready => "✓ ready",
        CompositionReadinessStatus.Flies => "flies",
        CompositionReadinessStatus.NotYet => "not yet",
        _ => "unknown"
    };
    public string ToFlyLabel => Status is CompositionReadinessStatus.Ready or CompositionReadinessStatus.Flies
        ? "—"
        : ToFly is { } time ? EveDurationFormatter.Format(time) : "—";
    public string ToMinLabel => Status == CompositionReadinessStatus.Ready
        ? "—"
        : ToMin is { } time ? EveDurationFormatter.Format(time) : "—";
    public IReadOnlyList<SkillGapViewModel> MissingSkills { get; } = missingSkills;
    public string QueueSummary { get; } = queueSummary;
}
