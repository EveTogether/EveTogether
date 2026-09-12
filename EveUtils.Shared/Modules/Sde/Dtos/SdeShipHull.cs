namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// One hull named in a site's <see cref="SdeSite.IncludedShipTypes"/> or <see cref="SdeSite.ExcludedShipTypes"/>
/// (ET-232), carrying the ship group it belongs to (ET-263) so a caller can bucket a homefront's sixteen named T1
/// cruisers under "Cruisers" rather than listing them loose.
/// </summary>
public sealed record SdeShipHull(int TypeId, string Name, int GroupId);
