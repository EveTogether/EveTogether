namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>
/// Display + machine metadata for one permission. <see cref="Code"/> is the stable key (used
/// over the wire / in groups); the rest is for the future control panel.
/// </summary>
public sealed record PermissionDescriptor(string Code, string DisplayName, string Description, string Module);
