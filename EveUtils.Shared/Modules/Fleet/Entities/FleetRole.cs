using System.Runtime.Serialization;

namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A member's role in the fleet hierarchy, in ESI parity. The <see cref="EnumMemberAttribute"/>
/// values are the literal ESI role strings, reserved for the later in-game coupling; EF still stores
/// the enum as its int value.
/// </summary>
public enum FleetRole
{
    [EnumMember(Value = "fleet_commander")]
    FleetCommander = 0,

    [EnumMember(Value = "wing_commander")]
    WingCommander = 1,

    [EnumMember(Value = "squad_commander")]
    SquadCommander = 2,

    [EnumMember(Value = "squad_member")]
    SquadMember = 3,

    /// <summary>
    /// App-side only: a member who is in the fleet but holds no wing/squad position — the result of
    /// "remove from composition". Not an ESI role (ESI has no unassigned state); the later in-game coupling maps
    /// it onto squad_member at fleet level. Stored as int 4 — no schema change, the Role column already exists.
    /// </summary>
    Unassigned = 4
}
