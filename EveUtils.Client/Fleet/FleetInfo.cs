using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Client-side view of a fleet as returned by <see cref="EveUtils.Client.Transport.FleetClient"/>. Maps the
/// gRPC <c>FleetDto</c> to the domain enums + parsed timestamps the UI binds to. <c>IsMine</c> is resolved
/// by the caller against the acting character (the server stamps the creator, not "mine").
/// </summary>
public sealed record FleetInfo(
    long Id,
    string Name,
    string? Description,
    FleetVisibility Visibility,
    FleetState State,
    int CreatorCharacterId,
    DateTimeOffset? FromTime,
    DateTimeOffset? ToTime,
    DateTimeOffset CreatedAt,
    FleetActivation Activation,
    long? FleetCompositionId = null,
    long? EsiFleetId = null,
    int? EsiFleetBossId = null,
    bool EsiAutoApplyStructure = false,
    bool EsiAutoInviteMembers = false,

    /// <summary>When the fleet was last started, or null when it never was — what "active for 01:26:27" is read
    /// from (ET-166). Only meaningful while <see cref="Activation"/> is Active; a Stop leaves it standing as the
    /// record of the last run and the next Start overwrites it.</summary>
    DateTimeOffset? ActivatedAt = null);
