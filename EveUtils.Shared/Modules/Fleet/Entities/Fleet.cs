namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A fleet = a composition/plan owned by <see cref="CreatorCharacterId"/>. The owner need not be a
/// member, a character may own several fleets, and creating one is not in-game-binding. The server it lives
/// on is the implicit scope (no server reference needed). ESI-parity fields (<see cref="EsiFleetId"/> etc.)
/// are reserved for the later in-game coupling, empty in v1.
/// </summary>
public sealed class Fleet
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public FleetVisibility Visibility { get; set; }

    /// <summary>Optional planned window. <see cref="ToTime"/> also speeds up cleanup.</summary>
    public DateTimeOffset? FromTime { get; set; }
    public DateTimeOffset? ToTime { get; set; }

    /// <summary>ESI id of the owning character. Not a member by definition.</summary>
    public int CreatorCharacterId { get; set; }

    public FleetOfflineBehavior OfflineBehavior { get; set; }
    public FleetState State { get; set; }

    /// <summary>In-game lifecycle phase: a fleet forms, then the creator starts it, then concludes it.
    /// Default <see cref="FleetActivation.Forming"/>. Independent of <see cref="State"/> (the soft-delete lifecycle).</summary>
    public FleetActivation Activation { get; set; }

    /// <summary>When the fleet was started (Forming → Active). Null until started. Tiebreaks the "one active fleet
    /// per character" rule: a character broadcasts only to the active fleet they were activated in first, so a
    /// member signed up in advance to a fleet that starts while they are still in an earlier active fleet is not
    /// coupled to the new one (2026-06-04). A null value sorts as earliest (pre-migration active fleets win ties).</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time the fleet saw activity (a member event); cleanup signal.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// A client-only fleet: it lives purely in the desktop client's local SQLite, is never published
    /// to a server, has no fleet browser, no remote invites/requests/notifications, and only ever holds the
    /// owner's own local characters plus externals. Its metrics stay local (the publisher emits
    /// <c>EventTarget.Local</c>, never over gRPC). Always <c>false</c> on the server — the column exists only in
    /// the client DB, so this flag is server-irrelevant and the same Shared entity/handlers serve both hosts.
    /// </summary>
    public bool IsClientOnly { get; set; }

    /// <summary>The coupled reusable doctrine, set by the owner in the Forming phase; null = none. One
    /// composition can be coupled to several fleets (1 composition → N fleets); a fleet has at most one.</summary>
    public long? FleetCompositionId { get; set; }

    // --- ESI-parity, reserved for in-game coupling; v1 leaves these empty/default. ---
    public string? Motd { get; set; }
    public bool IsFreeMove { get; set; }
    public bool IsRegistered { get; set; }
    public bool IsVoiceEnabled { get; set; }
    public long? EsiFleetId { get; set; }
    public int? EsiFleetBossId { get; set; }

    /// <summary>Whether this fleet is bound to a live in-game ESI fleet. Default <see cref="EsiFleetSyncState.NotLinked"/>.</summary>
    public EsiFleetSyncState EsiSyncState { get; set; }

    /// <summary>When true, additive structure changes (new wings/squads) are pushed to the live in-game fleet
    /// automatically as they happen, instead of waiting for a manual PUSH STRUCTURE. Additive only — the
    /// destructive removal of obsolete in-game units stays behind the manual confirm. A boss-client behaviour flag:
    /// it has no effect while the fleet is not coupled or the boss lacks <c>write_fleet</c>; the server only stores
    /// and relays it.</summary>
    public bool EsiAutoApplyStructure { get; set; }

    /// <summary>When true, a character joining the fleet or being assigned into structure receives an in-game ESI
    /// invite automatically, instead of a manual INVITE ROSTER. Idempotent — a pilot already in the live
    /// fleet is skipped. Same boss-client/coupled/<c>write_fleet</c> conditions as <see cref="EsiAutoApplyStructure"/>.</summary>
    public bool EsiAutoInviteMembers { get; set; }
}
