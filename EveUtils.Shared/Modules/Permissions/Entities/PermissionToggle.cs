namespace EveUtils.Shared.Modules.Permissions.Entities;

/// <summary>
/// EF-persisted on/off toggle per app-permission code: the server-DB row replacing the
/// former <c>permission-toggles.json</c> file. Server-only — the table lands in the server DB. A code with
/// no row defaults to enabled (default-allow). Distinct from the CQRS policy types in
/// <c>EveUtils.Shared.Cqrs.Permissions</c>; this is just the persisted state the admin panel flips.
/// </summary>
public sealed class PermissionToggle
{
    public string Code { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
