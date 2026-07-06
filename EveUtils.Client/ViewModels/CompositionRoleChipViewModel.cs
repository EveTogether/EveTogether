namespace EveUtils.Client.ViewModels;

/// <summary>A role-group chip on a composition card: the role label plus its pilot requirement —
/// a group minimum ("≥40") or a per-fit breakdown ("3+2"), or "—" when none is set.</summary>
public sealed record CompositionRoleChipViewModel(string RoleName, string MinLabel);
