namespace EveUtils.Shared.Modules.Killmails.Dtos;

/// <summary>Where a killmail's victim died: metres in the system's own frame, the sun at the origin (ET-473).</summary>
public sealed record KillmailPosition(double X, double Y, double Z);
