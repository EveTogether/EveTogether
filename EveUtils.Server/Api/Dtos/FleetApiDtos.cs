namespace EveUtils.Server.Api.Dtos;

/// <summary>
/// A fleet on this server as a directory row. Mirrors the Local API's fleet row in shape, minus the fields that
/// only mean something on a client: everything here is server-scoped by definition, so there is no local/server
/// scope to report. The enums travel as their names — a consumer reads "Active", not 0.
/// </summary>
public sealed record ApiFleetListItem(
    long Id,
    string Name,
    string? Description,
    int CreatorCharacterId,
    string State,
    string Activation,
    string Visibility,
    long? CompositionId);

/// <summary>
/// One fleet in full: its header, the wing/squad structure and the whole roster. <c>CompositionName</c> is the
/// coupled doctrine's name, null when none is coupled.
/// </summary>
public sealed record ApiFleetDetail(
    long Id,
    string Name,
    string? Description,
    int CreatorCharacterId,
    string State,
    string Activation,
    string Visibility,
    long? CompositionId,
    string? CompositionName,
    IReadOnlyList<ApiFleetWing> Wings,
    IReadOnlyList<ApiFleetMember> Members);

/// <summary>A wing of the fleet with its squads (its members reference it by <c>WingId</c>).</summary>
public sealed record ApiFleetWing(long Id, string Name, IReadOnlyList<ApiFleetSquad> Squads);

/// <summary>A squad within a wing (its members reference it by <c>SquadId</c>).</summary>
public sealed record ApiFleetSquad(long Id, string Name);

/// <summary>
/// One roster member: the pilot, their placement and role, and — when a fit is assigned — its ship type and fit
/// name. <c>WingId</c>/<c>SquadId</c> are <c>-1</c> when unassigned (the ESI sentinel the roster itself uses).
/// The character's name is not resolved here; <c>/characters</c> (M2) is where a consumer joins ids to names.
/// </summary>
public sealed record ApiFleetMember(
    long Id,
    int CharacterId,
    long WingId,
    long SquadId,
    string Role,
    bool IsExternal,
    int? ShipTypeId,
    string? FitName);
