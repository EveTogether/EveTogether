using EveUtils.Shared.Modules.Fleet.Composition;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// Roster membership: which character sits in which wing/squad with which role. Decoupled
/// from ownership; a character may be rostered in several fleets. <see cref="WingId"/>/<see cref="SquadId"/>
/// use the ESI sentinel <c>-1</c> for "not assigned" (so they are plain longs, not FK-constrained).
/// </summary>
public sealed class FleetMember
{
    public long Id { get; set; }
    public long FleetId { get; set; }
    public int CharacterId { get; set; }

    /// <summary>Assigned wing, or <c>-1</c> when unassigned (ESI sentinel).</summary>
    public long WingId { get; set; } = -1;

    /// <summary>Assigned squad, or <c>-1</c> when unassigned (ESI sentinel).</summary>
    public long SquadId { get; set; } = -1;

    public FleetRole Role { get; set; }
    public DateTimeOffset JoinTime { get; set; }

    /// <summary>
    /// A remote/external member added by the owner on trust: an EVE character with no client
    /// session on this server. They are a default-accepted member but never source live metrics or presence.
    /// </summary>
    public bool IsExternal { get; set; }

    public int? ShipTypeId { get; set; }
    public int? SolarSystemId { get; set; }
    public bool TakesFleetWarp { get; set; }

    /// <summary>The fit this member flies, as a self-contained snapshot; null = none assigned yet.</summary>
    public FitReference? AssignedFit { get; set; }

    /// <summary>The composition entry the assigned fit fills, if it came from the coupled composition;
    /// null when the member flies an own library/server fit outside the composition.</summary>
    public long? AssignedCompositionEntryId { get; set; }

    /// <summary>The pilot's own client's skill verdict for <see cref="AssignedFit"/>: skills
    /// never leave the pilot's client, so only the verdict travels. Reset to Unknown whenever the fit changes.</summary>
    public FitSkillVerdict FitSkillVerdict { get; set; }

    /// <summary>The in-game ESI fleet member id once this member is matched to the live fleet; null
    /// until linked. Distinct from <see cref="CharacterId"/> — ESI keys move/kick by this member id.</summary>
    public long? EsiMemberId { get; set; }
}
