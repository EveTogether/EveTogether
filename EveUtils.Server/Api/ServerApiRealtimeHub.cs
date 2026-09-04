using System.Security.Claims;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using Microsoft.AspNetCore.SignalR;

namespace EveUtils.Server.Api;

/// <summary>
/// The realtime channel of the read-only server API. It carries nothing yet — what is pushed over it waits for a
/// consumer to say what it must react to — so all this hub does is hold the door: the <c>/api/v1</c> group admits
/// the connection on the same key as every other route, and this keeps re-checking that key while it is open.
/// </summary>
public sealed class ServerApiRealtimeHub(
    IServiceScopeFactory scopes,
    ServerApiOptions options,
    ILogger<ServerApiRealtimeHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        // Detached: the watch has to outlive this call, and the connection must not wait on it.
        _ = _CloseWhenTheKeyStopsBeingValidAsync(Context);
        return base.OnConnectedAsync();
    }

    /// <summary>
    /// Re-reads the key behind an open connection and closes it once the key is gone, disabled or past its expiry.
    /// There is no revocation moment to hook — a key also dies by being deleted or edited in the database — so this
    /// poll is the only thing between a withdrawn key and a socket that keeps working. Every way it can go wrong
    /// ends in <see cref="HubCallerContext.Abort"/>: a watch that stopped quietly leaves a connection nobody checks
    /// again, and from the outside that is indistinguishable from one still being watched.
    /// </summary>
    private async Task _CloseWhenTheKeyStopsBeingValidAsync(HubCallerContext context)
    {
        try
        {
            string prefix = context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("A realtime connection arrived without an API-key identity.");

            // ponytail: one timer and one indexed read per open connection; a single sweep over all of them is the
            // upgrade the day this channel carries more than a handful.
            using var timer = new PeriodicTimer(options.RealtimeKeyRecheck);
            while (await timer.WaitForNextTickAsync(context.ConnectionAborted))
            {
                using IServiceScope scope = scopes.CreateScope();
                ApiKey? key = await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>()
                    .FindByPrefixAsync(prefix, context.ConnectionAborted);

                if (key is null || !key.IsActive || key.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    logger.LogInformation("Closing a realtime connection: API key {Prefix} is no longer valid.", prefix);
                    break;
                }
            }
        }
        // Filtered on the connection's own token: any other cancellation is a watch that stopped early, and that
        // must close the connection like every other failure instead of passing for a clean exit.
        catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-checking the API key of a realtime connection failed; closing the connection.");
        }

        context.Abort();
    }
}
