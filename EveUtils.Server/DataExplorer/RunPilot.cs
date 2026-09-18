using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Server.DataExplorer;

/// <summary>One pilot's own run inside a run group: every pilot pushes a Run row of their own.</summary>
public sealed class RunPilot
{
    public required Guid RunId { get; init; }
    public required long CharacterId { get; init; }

    /// <summary>The name the pilot's client knew when the run started; null on older runs.</summary>
    public string? NameSnapshot { get; init; }

    public required RunState State { get; init; }
    public required decimal BountyIsk { get; init; }
}
