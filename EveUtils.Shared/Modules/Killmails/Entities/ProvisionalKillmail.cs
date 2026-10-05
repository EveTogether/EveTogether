namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>
/// A killmail parsed from pasted clipboard text (ET-340), shown before the real ESI killmail (feed or a pasted
/// link) confirms it. Only the fields needed to match it against that real mail are stored — no items, no
/// attackers: a detail read re-parses <see cref="RawText"/> instead.
/// </summary>
public sealed class ProvisionalKillmail
{
    public Guid Id { get; set; }
    public int CharacterId { get; set; }
    public DateTime KillmailTimeUtc { get; set; }
    public required string VictimName { get; set; }
    public int VictimShipTypeId { get; set; }
    public required string RawText { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
