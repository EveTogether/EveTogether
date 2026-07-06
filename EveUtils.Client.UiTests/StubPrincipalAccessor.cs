using EveUtils.Shared.Identity;

namespace EveUtils.Client.UiTests;

/// <summary>A fixed <see cref="IPrincipalAccessor"/> for authorization tests. The composition authorizer only feeds
/// the principal to the access policy; the owner check uses the command's acting character, so the principal value
/// itself is irrelevant to the test outcome — it just has to exist.</summary>
internal sealed class StubPrincipalAccessor(Principal principal) : IPrincipalAccessor
{
    public Principal Current { get; } = principal;
}
