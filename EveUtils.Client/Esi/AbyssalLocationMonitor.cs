using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Notifications;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Location;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Watches <c>/characters/{id}/location/</c> all session, for every character with the location scope. Continuous
/// rather than per-run (ET-62) because the countdown anchors on the last moment the pilot was proven outside, and
/// only these polls can prove it — the gamelog writes nothing on the way in or out. Stop between runs and that anchor
/// ages without bound; polling keeps it within one <see cref="PollInterval"/>.
///
/// It reports the whole reading rather than an abyssal verdict, because ET-63's location bootstrap reads the same
/// polls to fill a system the gamelog has not named yet. Neither reader adds a call of its own.
/// </summary>
public sealed class AbyssalLocationMonitor(
    IEsiLocationClient locations,
    IToastService toasts,
    IServiceProvider services,
    ILogger<AbyssalLocationMonitor> logger) : IAbyssalLocationMonitor, ISingletonService, IDisposable
{
    /// <summary>
    /// 6 s, not the 5 s ESI caches this endpoint for: polling on the TTL re-serves the same cached body about half
    /// the time, so a hair over it makes every call a fresh reading. Same figure EVE Workbench's tracker settled on.
    /// Init-only so tests can shrink it; a suite cannot wait on real abyssal timings.
    /// </summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(6);

    // ~2 minutes of unbroken failure at the poll interval. Transient trouble must not drop a clock mid-run, but a
    // monitor that cannot read anything is only burning ESI budget. (EVE Workbench uses 20 at 6 s for the same call.)
    private const int MaxConsecutiveFailures = 20;

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    // A scopeless character refuses on its first poll and toasts; touching the dispatcher before the UI thread owns
    // it binds it to this one, and Avalonia's own start-up then dies on VerifyAccess (measured: no window at all).
    private readonly TaskCompletionSource _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The UI exists; watches may start polling (and may raise a toast). Idempotent.</summary>
    public void UiReady() => _uiReady.TrySetResult();

    // One warning per character per session. The watch runs all evening now, so without this the same toast would
    // return every time the failure counter trips; a restart is a fair moment to mention it again.
    private readonly ConcurrentDictionary<int, byte> _warned = new();

    /// <summary>
    /// Starts watching <paramref name="characterId"/> for the rest of the session. <paramref name="onReading"/> is
    /// called after every reading; a reading without a system id means the watch was lost and no clock can be
    /// trusted. Watching a character that is already watched does nothing.
    /// </summary>
    public void Watch(int characterId, Action<EsiLocationReading> onReading)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(characterId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => WatchAsync(characterId, onReading, cts.Token), CancellationToken.None);
    }

    public void Stop(int characterId)
    {
        if (!_running.TryRemove(characterId, out var cts))
            return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>The watch itself. <see cref="Watch"/> is only the fire-and-forget wrapper; tests drive this.</summary>
    internal async Task WatchAsync(int characterId, Action<EsiLocationReading> onReading, CancellationToken cancellationToken)
    {
        var failures = 0;

        try
        {
            await _uiReady.Task.WaitAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await locations.GetLocationAsync(characterId, cancellationToken);

                if (result is { IsSuccess: true, Value: { } location })
                {
                    failures = 0;
                    onReading(new EsiLocationReading(location.SolarSystemId, DateTime.UtcNow));
                }
                else if (Fatal(result.Error?.Kind))
                {
                    Warn(characterId, result.Error?.Kind);
                    Lost(characterId, onReading);
                    return;
                }
                else if (++failures > MaxConsecutiveFailures)
                {
                    logger.LogWarning("Abyssal monitor for {CharacterId} gave up after {Failures} failed location reads.",
                        characterId, failures);
                    Lost(characterId, onReading);
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() won, or the app is closing: whoever cancelled owns the run state.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Abyssal monitor for {CharacterId} stopped on an unexpected error.", characterId);
            Lost(characterId, onReading);
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
            ? "EVE Together may not read this character's location, so it cannot see abyssal runs at all — no "
              + "countdown will appear."
            : "This character's ESI sign-in no longer works, so EVE Together cannot see abyssal runs.";

        // Actions keep the toast on screen until it is answered, which is the point: the one thing that fixes this is
        // a sign-in the pilot has to start.
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

    private void Lost(int characterId, Action<EsiLocationReading> onReading)
    {
        if (_running.TryRemove(characterId, out var cts))
            cts.Dispose();
        onReading(EsiLocationReading.Lost(DateTime.UtcNow));
    }

    public void Dispose()
    {
        foreach (var characterId in _running.Keys)
            Stop(characterId);
    }
}
