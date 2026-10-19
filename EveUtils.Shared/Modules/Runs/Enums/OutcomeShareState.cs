namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where this client stands with one activity's shared outcome on one server (ET-515).</summary>
public enum OutcomeShareState
{
    /// <summary>Waiting to be sent or sent again; whatever failed is retried.</summary>
    Pending,

    /// <summary>The server holds this client's outcome as of <c>SentRevision</c>.</summary>
    Shared,

    /// <summary>A fleet mate shared the activity first, and one outcome per activity is kept: nothing of this client's
    /// is on the server, and nothing is owed to it.</summary>
    Covered,

    /// <summary>The server refused the outcome as it stands; sent again only once the activity changes.</summary>
    Rejected,

    /// <summary>Gone locally (deleted, discarded, or no longer counted) after it was shared: the server is still to be
    /// told, with a tombstone.</summary>
    RetractPending
}
