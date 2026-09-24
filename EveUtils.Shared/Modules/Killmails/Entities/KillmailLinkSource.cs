namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>How a killmail got linked to a run. A <see cref="Manual"/> link is the pilot's and is never overwritten.</summary>
public enum KillmailLinkSource
{
    None = 0,
    Auto = 1,
    Manual = 2
}
