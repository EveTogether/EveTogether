using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.UiTests;

/// <summary>A fixed-verdict <see cref="IAccessPolicy"/> for authorization tests: lets a test simulate the v2
/// group-based policy granting (true) or refusing (false) <c>fleet-composition.manage</c>, instead of the v1
/// <c>OwnerAllPolicy</c> which always allows. Refusing is what proves the owner-or-right gate actually denies.</summary>
internal sealed class StubAccessPolicy(bool allowed) : IAccessPolicy
{
    public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default)
        => Task.FromResult(allowed);
}
