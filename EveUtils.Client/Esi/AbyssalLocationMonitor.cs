using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Notifications;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Location;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Watches <c>/characters/{id}/location/</c> for one abyssal run, and only then.
///
/// The gamelog can see a pilot go in but never come out — you leave where you fired the filament, so nothing is
/// written there. ESI can: the first poll outside <see cref="AbyssalSpace.IsAbyssalSystem"/> ends the run. Starting
/// on the log keeps this to one run's worth of calls instead of polling all evening. See ET-56.
/// </summary>
public sealed class AbyssalLocationMonitor(
    IEsiLocationClient locations,
    IToastService toasts,
    IServiceProvider services,
    ILogger<AbyssalLocationMonitor> logger) : IAbyssalLocationMonitor, ISingletonService, IDisposable
{
    // Both are init-only so tests can shrink them; a suite cannot wait on real abyssal timings.

    /// <summary>
    /// 6 s, not the 5 s ESI caches this endpoint for: polling on the TTL re-serves the same cached body about half
    /// the time, so a hair over it makes every call a fresh reading. Same figure EVE Workbench's tracker settled on.
    /// </summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long to keep watching before giving up. NOT the countdown: that is <see cref="AbyssalSpace.RunLimit"/>,
    /// counted from the last moment the pilot was proven outside. This one is counted from when the watch STARTS,
    /// which is the first abyssal shot in the log — up to 3.5 minutes into the run (measured 2026-08-29). The exit
    /// therefore always falls inside 20 minutes of here, usually well inside, so the same number is a roomy net
    /// rather than a deadline.
    /// </summary>
    internal TimeSpan WatchTimeout { get; init; } = TimeSpan.FromMinutes(20);

    // ~2 minutes of unbroken failure at the poll interval. Transient trouble must not drop a clock mid-run, but a
    // monitor that cannot read anything is only burning ESI budget. (EVE Workbench uses 20 at 6 s for the same call.)
    private const int MaxConsecutiveFailures = 20;

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    // One warning per character per session. Runs come in threes on an evening and the toast persists until it is
    // answered, so repeating it would nag rather than inform; a restart is a fair moment to mention it again.
    private readonly ConcurrentDictionary<int, byte> _warned = new();

    /// <summary>
    /// Starts watching <paramref name="characterId"/> until it is seen outside the abyss, at which point
    /// <paramref name="onRunEnded"/> is called with the moment of that sighting — or with null when the monitor gives
    /// up, so a stale countdown is cleared either way. Starting a character that is already watched does nothing.
    /// </summary>
    public void Start(int characterId, Action<DateTime?> onRunEnded)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(characterId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => WatchAsync(characterId, onRunEnded, cts.Token), CancellationToken.None);
    }

    public void Stop(int characterId)
    {
        if (!_running.TryRemove(characterId, out var cts))
            return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>The watch itself. <see cref="Start"/> is only the fire-and-forget wrapper; tests drive this.</summary>
    internal async Task WatchAsync(int characterId, Action<DateTime?> onRunEnded, CancellationToken cancellationToken)
    {
        var giveUpAt = DateTime.UtcNow + WatchTimeout;
        var failures = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < giveUpAt)
            {
                var result = await locations.GetLocationAsync(characterId, cancellationToken);

                if (result is { IsSuccess: true, Value: { } location })
                {
                    failures = 0;
                    if (!AbyssalSpace.IsAbyssalSystem(location.SolarSystemId))
                    {
                        // Also the correction for a false start: the gamelog's name list is short, so a normal-space
                        // fight can open a run that was never real. One poll takes it back.
                        Finish(characterId, onRunEnded, DateTime.UtcNow);
                        return;
                    }
                }
                else if (Fatal(result.Error?.Kind))
                {
                    Warn(characterId, result.Error?.Kind);
                    // Stop calling, but let the timeout still clear the countdown — we cannot see the pilot leave,
                    // and a clock nobody can end is worse than one that expires.
                    break;
                }
                else if (++failures > MaxConsecutiveFailures)
                {
                    logger.LogWarning("Abyssal monitor for {CharacterId} gave up after {Failures} failed location reads.",
                        characterId, failures);
                    break;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            var remaining = giveUpAt - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);

            Finish(characterId, onRunEnded, null);
        }
        catch (OperationCanceledException)
        {
            // Stop() won, or the app is closing: whoever cancelled owns the run state.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Abyssal monitor for {CharacterId} stopped on an unexpected error.", characterId);
            Finish(characterId, onRunEnded, null);
        }
    }

    // Only a refusal that the next poll cannot fix: no scope, and no working token. Everything else — 5xx, timeouts,
    // rate limits, ESI being down — is transient and goes through the failure counter instead.
    private static bool Fatal(EsiErrorKind? kind) => kind is EsiErrorKind.ScopeMissing or EsiErrorKind.AuthRequired;

    private void Warn(int characterId, EsiErrorKind? kind)
    {
        logger.LogWarning("Abyssal monitor for {CharacterId} stopped: {Kind}.", characterId, kind);

        if (!_warned.TryAdd(characterId, 0))
            return;

        var why = kind == EsiErrorKind.ScopeMissing
            ? "EVE Together may not read this character's location, so it cannot tell when you leave the abyss — the "
              + "countdown will run to the end instead of stopping when you are out."
            : "This character's ESI sign-in no longer works, so the countdown cannot tell when you leave the abyss.";

        // Actions keep the toast on screen until it is answered, which is the point: this arrives mid-run and the
        // one thing that fixes it is a sign-in the pilot has to start.
        toasts.Show("No abyssal detection", why, ToastKind.Warning,
        [
            new ToastAction("Not now", () => { }),
            new ToastAction("Allow location", () => _ = GrantLocationAsync(characterId), ToastActionStyle.Affirmative),
        ]);
    }

    // Re-auth rather than a fresh sign-in: the granted set is kept and the location scope is added to it.
    private async Task GrantLocationAsync(int characterId)
    {
        try
        {
            if (services.GetService<LocalEsiLoginService>() is { } login)
                await login.ReAuthenticateAsync(characterId, [LocationScopeCatalog.ReadLocation]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-authentication for location access failed for {CharacterId}.", characterId);
            toasts.Show("Sign-in failed", "Could not add location access. Try again from the character's ESI menu.",
                ToastKind.Error);
        }
    }

    private void Finish(int characterId, Action<DateTime?> onRunEnded, DateTime? seenOutsideUtc)
    {
        if (_running.TryRemove(characterId, out var cts))
            cts.Dispose();
        onRunEnded(seenOutsideUtc);
    }

    public void Dispose()
    {
        foreach (var characterId in _running.Keys)
            Stop(characterId);
    }
}
