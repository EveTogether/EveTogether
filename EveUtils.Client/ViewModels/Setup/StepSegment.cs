namespace EveUtils.Client.ViewModels.Setup;

/// <summary>One bar of the wizard's stepper.</summary>
public sealed class StepSegment(string label, StepSegmentState state, bool isOptional, bool isLoop)
{
    public string Label { get; } = label;
    public bool IsOptional { get; } = isOptional;
    public bool IsDone => state is StepSegmentState.Done;
    public bool IsCurrent => state is StepSegmentState.Current;

    /// <summary>An upcoming step of the per-character loop, drawn dashed.</summary>
    public bool IsLoop { get; } = isLoop && state is StepSegmentState.Upcoming;

    public bool IsPlain => !IsDone && !IsCurrent && !IsLoop;
}
