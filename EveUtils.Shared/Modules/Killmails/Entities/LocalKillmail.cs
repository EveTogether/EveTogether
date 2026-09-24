namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>
/// A kill or loss of a character, imported from ESI and keyed by (CharacterId, KillmailId). Ids only: names come
/// from the SDE or the name lookup at read time, and the ISK value is priced at read time too.
/// </summary>
public sealed class LocalKillmail
{
    public int CharacterId { get; set; }
    public int KillmailId { get; set; }
    public required string Hash { get; set; }
    public DateTime KillmailTimeUtc { get; set; }
    public int SolarSystemId { get; set; }

    /// <summary>True when the victim is this character.</summary>
    public bool IsLoss { get; set; }

    public int VictimShipTypeId { get; set; }
    public int? VictimCharacterId { get; set; }
    public int? VictimCorporationId { get; set; }
    public int? VictimAllianceId { get; set; }
    public int DamageTaken { get; set; }

    public Guid? RunId { get; set; }
    public KillmailLinkSource LinkSource { get; set; }
    public DateTime ImportedAtUtc { get; set; }

    public List<LocalKillmailItem> Items { get; set; } = [];
    public List<LocalKillmailAttacker> Attackers { get; set; } = [];
}
