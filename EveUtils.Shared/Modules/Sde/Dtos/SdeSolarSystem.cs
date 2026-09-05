namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A solar system from the SDE's own <c>SolarSystem</c> table (ET-173's mission import, all 8490 rows carrying
/// name and security — bijvangst for ET-127): a destination typed into the escalation dialog resolves here without
/// ESI, since the SDE already has the id and the security status the Agency window showed.
/// </summary>
public sealed record SdeSolarSystem(int SolarSystemId, string Name, double SecurityStatus);
