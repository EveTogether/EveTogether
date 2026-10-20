using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>What this client knows of one activity's outcome on one server (ET-515): the key to send an update or a
/// tombstone under, and what to count in the settings. Client only, and not a copy of the outcome: that is read again
/// from the runs whenever it is sent.</summary>
public sealed class OutcomeShareRecord
{
    public string ServerAddress { get; set; } = null!;

    /// <summary>The activity's <c>GroupCode ?? Id</c>.</summary>
    public string ActivityRef { get; set; } = null!;

    /// <summary>The pilot whose session shares it; a withdraw is asked once per pilot.</summary>
    public long CharacterId { get; set; }

    public OutcomeShareState State { get; set; }

    /// <summary>The sum of the activity's run revisions this was last prepared from; unchanged means nothing to send.</summary>
    public long SourceRevision { get; set; }

    /// <summary>The revision last sent (or to be sent): always above the one before, whatever the runs' own revisions did.</summary>
    public long SentRevision { get; set; }

    public DateTime? SharedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
