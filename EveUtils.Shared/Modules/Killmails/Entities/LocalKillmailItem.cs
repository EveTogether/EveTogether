namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>
/// One (Flag, TypeId, IsNested) line of a killmail's victim items, with ESI stacks of the same key summed. A nested
/// item carries the flag of its top-level container; the ship itself is <see cref="LocalKillmail.VictimShipTypeId"/>.
/// </summary>
public sealed class LocalKillmailItem
{
    public int CharacterId { get; set; }
    public int KillmailId { get; set; }

    /// <summary>The raw ESI inventory flag; translating it to a slot name belongs to the reader.</summary>
    public int Flag { get; set; }

    public int TypeId { get; set; }
    public bool IsNested { get; set; }
    public long QuantityDestroyed { get; set; }
    public long QuantityDropped { get; set; }
}
