namespace EveUtils.Shared.Identity;

/// <summary>
/// The actor on whose behalf an operation runs and who owns the data (foundation pillar 1).
/// <see cref="OwnerId"/> is the stable owner key used for ownership stamping; v1 client = the
/// single local owner ("local"), v2 server = the authenticated session subject.
/// </summary>
public sealed record Principal(string OwnerId, Character? Character);
