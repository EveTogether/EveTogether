using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a roster member (gRPC <c>MemberDto</c>), for the fleet roster tree.
/// <paramref name="AssignedFit"/> is the fit the member flies, or null when none is assigned;
/// <paramref name="AssignedCompositionEntryId"/> is the coupled-composition entry that fit fills (the doctrine
/// join key for the two-level fill overview), or null when the member flies an own fit outside the doctrine.
/// <paramref name="FitSkillVerdict"/> is the pilot's own client's can-fly verdict for that fit,
/// the badge fallback for pilots whose skills this client does not know locally.
/// <paramref name="LastSeenAt"/> is when the server last saw this member's client publish into the fleet, or null when
/// it never has — the difference between a pilot who left and one we have simply never heard from (ET-70).
/// <paramref name="Availability"/> is this member's self-reported availability for the fleet's next start (ET-169),
/// set and cleared by the member only; <paramref name="AvailabilityNote"/> is the optional short note that came with it.</summary>
public sealed record FleetMemberInfo(
    long Id,
    int CharacterId,
    long WingId,
    long SquadId,
    FleetRole Role,
    bool IsExternal,
    FitReferenceInfo? AssignedFit = null,
    long? AssignedCompositionEntryId = null,
    FitSkillVerdict FitSkillVerdict = FitSkillVerdict.Unknown,
    DateTimeOffset? LastSeenAt = null,
    FleetMemberAvailability Availability = FleetMemberAvailability.NotSet,
    string? AvailabilityNote = null);
