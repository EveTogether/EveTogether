namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>The SHOW row on the KILLMAILS overview (ET-332): one filter is on at a time, unlike the RUNS TYPES/CHARACTERS
/// tiles which each toggle independently.</summary>
public enum KillmailShowFilter
{
    All,
    Kills,
    Losses,
    NotLinked
}
