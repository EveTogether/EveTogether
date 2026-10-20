using EveUtils.Shared.Modules.RunOutcomes.Dtos;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One activity as it would be shared right now (ET-515).</summary>
public sealed class ActivityOutcomeShareDto
{
    public required string ActivityRef { get; init; }

    /// <summary>The pilot whose run opened the activity: the session the outcome is shared with.</summary>
    public required long CharacterId { get; init; }

    /// <summary>The sum of the revisions of every run of the activity, deleted ones included: it only goes up when any
    /// of them is corrected, deleted, restored or added.</summary>
    public required long Revision { get; init; }

    /// <summary>The server the activity already belongs to as far as the runs say: its fleet's, or the one a run was
    /// published to.</summary>
    public string? PreferredServer { get; init; }

    /// <summary>Null when the activity is not shareable now: deleted, discarded, not saved yet, or without a key.</summary>
    public RunOutcomeShareDto? Outcome { get; init; }
}
