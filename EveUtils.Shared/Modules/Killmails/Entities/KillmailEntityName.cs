namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>
/// A cached public name for a character, corporation or alliance id seen on a killmail. <see cref="Name"/> is
/// null when ESI does not know the id (a 404): that row still prevents a re-fetch until <see cref="RefreshedAtUtc"/>
/// falls outside the configured refresh interval.
/// </summary>
public sealed class KillmailEntityName
{
    public long Id { get; set; }
    public KillmailEntityKind Kind { get; set; }
    public string? Name { get; set; }
    public DateTime RefreshedAtUtc { get; set; }
}
