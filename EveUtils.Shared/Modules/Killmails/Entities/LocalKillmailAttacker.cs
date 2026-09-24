namespace EveUtils.Shared.Modules.Killmails.Entities;

/// <summary>
/// One attacker on a killmail, NPCs included, keyed by its position (<see cref="Ordinal"/>) in ESI's list. The
/// corporation and alliance are those at the time of the kill.
/// </summary>
public sealed class LocalKillmailAttacker
{
    public int CharacterId { get; set; }
    public int KillmailId { get; set; }
    public int Ordinal { get; set; }
    public int? AttackerCharacterId { get; set; }
    public int? CorporationId { get; set; }
    public int? AllianceId { get; set; }
    public int? FactionId { get; set; }
    public int? ShipTypeId { get; set; }
    public int? WeaponTypeId { get; set; }
    public int DamageDone { get; set; }
    public bool FinalBlow { get; set; }
}
