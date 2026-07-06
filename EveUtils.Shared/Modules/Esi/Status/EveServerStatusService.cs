using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi.Status;

/// <summary>
/// Polls the public ESI <c>GET /status/</c> endpoint and drives the shared <see cref="IEsiAvailabilityState"/> (and the
/// outage detector's recovery reset) for whichever host runs it — the client hosts it for its Tranquility status bar,
/// the server hosts it so its own ESI gate actually engages during downtime. While ESI is up it runs every 30 s — the
/// endpoint's cache TTL — so we never poll below the cache window; while it's down it polls every 15 s so
/// recovery is detected promptly without the per-second hammering a retried poll would cause (the poll is exempt from
/// retries, see <see cref="EsiStatusEndpoint"/>). The call is public (no character/token), so it works with no
/// characters signed in.
/// </summary>
public sealed class EveServerStatusService(
    IEsiClient esi,
    IEsiAvailabilityState availability,
    IEsiOutageDetector outageDetector,
    ILogger<EveServerStatusService> logger) : BackgroundService
{
    private static readonly TimeSpan UpPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownPollInterval = TimeSpan.FromSeconds(15);
    private const string StatusPath = EsiStatusEndpoint.Path;

    // Completed by the outage detector to cut the inter-poll wait short when a burst of failures suggests ESI is down.
    private volatile TaskCompletionSource _poke = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The wait until the next poll: tighter while ESI is down so recovery is picked up quickly. Public for testing.</summary>
    public static TimeSpan NextDelay(bool usable) => usable ? UpPollInterval : DownPollInterval;

    /// <summary>The latest known status; <see cref="EveServerStatusSnapshot.Unknown"/> until the first poll.</summary>
    public EveServerStatusSnapshot Current { get; private set; } = EveServerStatusSnapshot.Unknown;

    /// <summary>Raised off the UI thread when a poll changes the snapshot; subscribers marshal to their own thread.</summary>
    public event Action<EveServerStatusSnapshot>? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        outageDetector.OutageSuspected += OnOutageSuspected;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error polling Tranquility status.");
                }

                // Wait out the interval, but cut it short if the detector reports a burst of failures (likely an outage).
                var poke = _poke;
                await Task.WhenAny(Task.Delay(NextDelay(availability.IsUsable), stoppingToken), poke.Task);
                if (stoppingToken.IsCancellationRequested)
                    return;
                _poke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally
        {
            outageDetector.OutageSuspected -= OnOutageSuspected;
        }
    }

    private void OnOutageSuspected() => _poke.TrySetResult();

    /// <summary>Polls <c>/status/</c> once, updates <see cref="Current"/> and raises <see cref="Changed"/> on a change.</summary>
    public async Task<EveServerStatusSnapshot> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var result = await esi
            .GetAsync<EveServerStatusResponse>(StatusPath, cancellationToken: cancellationToken);

        // A failed /status/ poll (5xx or unreachable) means normal calls are pointless — gate them; a
        // successful poll (Online/VIP) re-opens the gate. The /status/ poll itself stays exempt.
        // Log only the transition (down/recovered) once, so an outage doesn't spam the log on every poll.
        var next = result.IsSuccess ? EsiAvailability.Available : EsiAvailability.Maintenance;
        if (next != availability.Current)
        {
            if (next == EsiAvailability.Maintenance)
                logger.LogWarning("ESI /status/ poll failed — gating non-essential calls until Tranquility is back.");
            else
            {
                logger.LogInformation("ESI /status/ poll succeeded — Tranquility is reachable again.");
                outageDetector.Reset(); // clear any stale failure run so the reopened gate doesn't instantly re-trip
            }
        }
        availability.Set(next);

        var snapshot = EveServerStatusSnapshot.From(result);
        if (snapshot != Current)
        {
            Current = snapshot;
            Changed?.Invoke(snapshot);
        }

        return snapshot;
    }
}
