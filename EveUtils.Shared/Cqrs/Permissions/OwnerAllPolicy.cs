using EveUtils.Shared.Identity;

namespace EveUtils.Shared.Cqrs.Permissions;

/// <summary>v1 policy: the single owner has every permission. v2 replaces this with groups.</summary>
public sealed class OwnerAllPolicy : IAccessPolicy
{
    public Task<bool> IsAllowedAsync(Principal principal, string code, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
