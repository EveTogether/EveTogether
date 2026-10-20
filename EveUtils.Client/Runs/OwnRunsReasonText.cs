namespace EveUtils.Client.Runs;

/// <summary>What a screen says when it shows the pilot's own runs where a server's figure could have been (ET-516, ET-517):
/// the run window's panel and the runs overview word it the same.</summary>
public static class OwnRunsReasonText
{
    // The run window's panel sets "Turn on sharing" apart as a link to Settings, so the sentence comes in three parts.
    public const string NotSharingLead = "Showing your own runs. ";
    public const string NotSharingLink = "Turn on sharing";
    public const string NotSharingTail = " in Settings to include the other pilots' runs.";

    public static string For(OwnRunsReason reason) => reason switch
    {
        OwnRunsReason.NotSharing => NotSharingLead + NotSharingLink + NotSharingTail,
        OwnRunsReason.TooFewPilots => "Too few pilots share this site yet, so this is your own runs.",
        OwnRunsReason.TooFewSharedRuns => "The pilots sharing this site have too few runs yet, so this is your own runs.",
        OwnRunsReason.ServerUnavailable => "The server could not be reached, so this is your own runs.",
        _ => string.Empty
    };

    /// <summary>The same reason as the tail of a one-line footer: "own runs (too few pilots share this site)".</summary>
    public static string Short(OwnRunsReason reason) => reason switch
    {
        OwnRunsReason.NotSharing => "own runs (sharing is off)",
        OwnRunsReason.TooFewPilots => "own runs (too few pilots share this site)",
        OwnRunsReason.TooFewSharedRuns => "own runs (the pilots sharing this site have too few runs)",
        OwnRunsReason.ServerUnavailable => "own runs (server unreachable)",
        _ => string.Empty
    };
}
