namespace EveUtils.Shared.Identity;

/// <summary>
/// Supplies the current <see cref="Principal"/> (foundation seam). The client provides the
/// local owner; a server host later provides the session principal. Feeds ownership stamping
/// (pillar 4) and the permission gate (pillar 2).
/// </summary>
public interface IPrincipalAccessor
{
    Principal Current { get; }
}
