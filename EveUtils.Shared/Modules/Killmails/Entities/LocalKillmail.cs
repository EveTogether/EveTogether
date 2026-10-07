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

    /// <summary>Where the victim died, in metres in the system's own frame (ESI <c>victim.position</c>, ET-473). Null for a
    /// mail stored before the position was kept, until the killmail refresh fills it in from ESI.</summary>
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public double? PositionZ { get; set; }

    public Guid? RunId { get; set; }
    public KillmailLinkSource LinkSource { get; set; }
    public DateTime ImportedAtUtc { get; set; }

    /// <summary>The fleet a fleet mate shared this mail in (ET-371); null for an own import. A newer share of that fleet
    /// reconciles only these rows, so own mails and another fleet's history are never removed by it.</summary>
    public long? SharedFromFleetId { get; set; }

    /// <summary>The server that fleet lives on, as <see cref="Fleet.FleetServerIdentity"/> reads it ("local" for a
    /// client-only fleet); a fleet id alone is only unique per server. Null for an own import.</summary>
    public string? SharedFromServer { get; set; }

    public List<LocalKillmailItem> Items { get; set; } = [];
    public List<LocalKillmailAttacker> Attackers { get; set; } = [];
}
