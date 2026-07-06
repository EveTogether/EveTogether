namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A reusable doctrine: a set of role-groups, each with its own fit-entries and minima, that an owner
/// composes once and couples to one or more not-yet-started fleets. Owned by <see cref="OwnerCharacterId"/>; the
/// server it lives on is the implicit scope. Mutations are gated on owner-or-<c>fleet-composition.manage</c>
/// enforced server-side. <see cref="IsClientOnly"/> mirrors <c>Fleet.IsClientOnly</c>: a client-only
/// composition lives purely in the desktop client's local SQLite and is owner-only (no server RBAC).
/// </summary>
public sealed class FleetComposition
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>ESI id of the owning character. Need not be a fleet member.</summary>
    public int OwnerCharacterId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// A client-only composition lives purely in the client's local SQLite, is never published to a server and is
    /// owner-only (no RBAC). Always <c>false</c> on the server — the column exists only in the client DB, so the
    /// same Shared entity/handlers serve both hosts (analogous to <c>Fleet.IsClientOnly</c>).
    /// </summary>
    public bool IsClientOnly { get; set; }
}
