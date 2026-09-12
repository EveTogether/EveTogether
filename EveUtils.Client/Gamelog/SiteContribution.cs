namespace EveUtils.Client.Gamelog;

/// <summary>
/// Something a character's own gamelog shows them doing TO a site, as opposed to having done to them (ET-230): the
/// interactions EVE's homefront rule names (domain/homefronts.md §4.1) that a gamelog carries at all. Mining is not
/// here — it already lands on the run itself (ET-229). Delivering cargo is not either: no gamelog line says it.
/// </summary>
public enum SiteContribution
{
    Damage,
    RemoteRepair,
    RemoteCapacitor,
    Salvage
}
