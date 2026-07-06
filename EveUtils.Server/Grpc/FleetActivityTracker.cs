using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Keeps a fleet's cleanup activity clock fresh from live fleet traffic. Combat/DPS never goes through a
/// roster command, so without this an actively-playing fleet's <c>LastActivityAt</c> is frozen at the last join and
/// the cleanup sweep treats it as stale — archiving it the moment everyone briefly disconnects (e.g. a client restart
/// mid-fight). Every fleet-scoped event (the ~1 Hz metric stream while members participate) notes activity, throttled
/// to ~1/min per fleet so the DB is not hammered. A connected member therefore keeps its fleet alive via the timestamp,
/// not only via the live-connection check, so a brief disconnect no longer loses the fleet.
/// </summary>
public sealed class FleetActivityTracker(IServiceProvider services) : ISingletonService
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastNoted = new();

    public async Task NoteAsync(long fleetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var shouldTouch = false;
        _lastNoted.AddOrUpdate(
            fleetId,
            _ => { shouldTouch = true; return now; },
            (_, previous) =>
            {
                if (now - previous < Throttle)
                    return previous;
                shouldTouch = true;
                return now;
            });

        if (!shouldTouch)
            return;

        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        await repository.TouchActivityAsync(fleetId, now, cancellationToken);
    }
}
