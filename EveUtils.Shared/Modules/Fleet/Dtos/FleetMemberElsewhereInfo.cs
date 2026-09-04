namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// One roster member who is on this fleet's roster and yet counts for another started fleet (ET-168). "Elsewhere
/// active", never "offline": offline means not logged in, and the two get mistaken for one another precisely
/// because both end in a pilot who contributes nothing here.
/// </summary>
/// <param name="CharacterId">The member of the fleet that was asked about.</param>
/// <param name="ElsewhereFleetId">The started fleet they count for instead.</param>
/// <param name="ElsewhereFleetName">Its name, so the caller can say where they are without a second lookup.</param>
public sealed record FleetMemberElsewhereInfo(int CharacterId, long ElsewhereFleetId, string ElsewhereFleetName);
