namespace EveUtils.Client.Runs;

/// <summary>What a screen says when it shows the pilot's own runs where a server's figure could have been (ET-516, ET-517):
/// the run window's panel and the runs overview word it the same.</summary>
public static class OwnRunsReasonText
{
    public static string For(OwnRunsReason reason) => reason switch
    {
        OwnRunsReason.NotSharing => "Showing your own runs. Turn on sharing in Settings to include the other pilots' runs.",
        OwnRunsReason.TooFewPilots => "Too few pilots share this site yet, so this is your own runs.",
        OwnRunsReason.TooFewSharedRuns => "The pilots sharing this site have too few runs yet, so this is your own runs.",
        OwnRunsReason.ServerUnavailable => "The server could not be reached, so this is your own runs.",
        _ => string.Empty
    };
}
