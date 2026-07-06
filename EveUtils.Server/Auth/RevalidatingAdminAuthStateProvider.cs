using System.Security.Claims;
using EveUtils.Shared.Modules.AdminAuth.Repositories;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server.Auth;

/// <summary>
/// Revalidates the admin cookie against the live DB on an interval: if the user no longer exists, is
/// deactivated, or their effective permissions/super-admin status changed (security-stamp mismatch), the
/// circuit's authentication state is invalidated and the user is forced to sign in again.
/// </summary>
internal sealed class RevalidatingAdminAuthStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        if (!int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAdminAuthRepository>();

        var user = await repository.GetUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
            return false;

        var currentStamp = await AdminClaims.ComputeStampAsync(repository, user, cancellationToken);
        return string.Equals(principal.FindFirst(AdminClaims.Stamp)?.Value, currentStamp, StringComparison.Ordinal);
    }
}
