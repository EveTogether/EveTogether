namespace EveUtils.Shared.Identity;

/// <summary>
/// Fixed principal for a host without per-call identity (e.g. the server's own internal actions in
/// the POC). The real server maps an authenticated session → principal (v2).
/// </summary>
public sealed class StaticPrincipalAccessor(Principal principal) : IPrincipalAccessor
{
    public Principal Current { get; } = principal;
}
