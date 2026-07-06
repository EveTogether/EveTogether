using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a roster member (gRPC <c>MemberDto</c>), for the fleet roster tree.
/// <paramref name="AssignedFit"/> is the fit the member flies, or null when none is assigned;
/// <paramref name="AssignedCompositionEntryId"/> is the coupled-composition entry that fit fills (the doctrine
/// join key for the two-level fill overview), or null when the member flies an own fit outside the doctrine.
/// <paramref name="FitSkillVerdict"/> is the pilot's own client's can-fly verdict for that fit,
/// the badge fallback for pilots whose skills this client does not know locally.</summary>
public sealed record FleetMemberInfo(
    long Id,
    int CharacterId,
    long WingId,
    long SquadId,
    FleetRole Role,
    bool IsExternal,
    FitReferenceInfo? AssignedFit = null,
    long? AssignedCompositionEntryId = null,
    FitSkillVerdict FitSkillVerdict = FitSkillVerdict.Unknown);
