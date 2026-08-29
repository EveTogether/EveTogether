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
/// Watches <c>/characters/{id}/location/</c> for as long as the app is running, for every character that granted the
/// location scope.
///
/// ET-62 moved abyssal detection here wholesale. The gamelog cannot see the way in OR the way out — a filament writes
/// nothing, and you leave where you fired it — so the log could only ever guess at a run from the names that shot
/// back, and could only ever anchor the clock on the last place it happened to see the pilot. Measured 2026-08-29:
/// an undock at 20:54:17 was still anchoring a run that started at 21:40:18, so the countdown was born 25:51 past its
/// own end and read "--:--" for the whole run.
///
/// Polling continuously removes the guess on both sides. <see cref="AbyssalSpace.IsAbyssalSystem"/> is a closed,
/// enumerated id range, so entry and exit are observed rather than inferred, and the anchor is never older than one
/// <see cref="PollInterval"/> — the floor the "+" promises becomes tight instead of unbounded.
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

    // The watch now starts while the app is still booting, and a character without the scope refuses on its very
    // first poll — which raises a toast. Touching Avalonia's dispatcher before the UI thread owns it binds it to
    // whatever thread got there first, and the real startup then dies on VerifyAccess. So every watch waits here.
    private readonly TaskCompletionSource _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The UI exists; watches may start polling (and may raise a toast). Idempotent.</summary>
    public void UiReady() => _uiReady.TrySetResult();

    // One warning per character per session. The watch runs all evening now, so without this the same toast would
    // return every time the failure counter trips; a restart is a fair moment to mention it again.
    private readonly ConcurrentDictionary<int, byte> _warned = new();

    /// <summary>
    /// Starts watching <paramref name="characterId"/> for the rest of the session. <paramref name="onPresence"/> is
    /// called after every reading: <c>true</c> = inside abyssal space, <c>false</c> = outside (which is also the
    /// anchor the next run will use), <c>null</c> = the watch was lost and no clock can be trusted. Watching a
    /// character that is already watched does nothing.
    /// </summary>
    public void Watch(int characterId, Action<bool?, DateTime> onPresence)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(characterId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => WatchAsync(characterId, onPresence, cts.Token), CancellationToken.None);
    }

    public void Stop(int characterId)
    {
        if (!_running.TryRemove(characterId, out var cts))
            return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>The watch itself. <see cref="Watch"/> is only the fire-and-forget wrapper; tests drive this.</summary>
    internal async Task WatchAsync(int characterId, Action<bool?, DateTime> onPresence, CancellationToken cancellationToken)
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
                    onPresence(AbyssalSpace.IsAbyssalSystem(location.SolarSystemId), DateTime.UtcNow);
                }
                else if (Fatal(result.Error?.Kind))
                {
                    Warn(characterId, result.Error?.Kind);
                    Lost(characterId, onPresence);
                    return;
                }
                else if (++failures > MaxConsecutiveFailures)
                {
                    logger.LogWarning("Abyssal monitor for {CharacterId} gave up after {Failures} failed location reads.",
                        characterId, failures);
                    Lost(characterId, onPresence);
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
            Lost(characterId, onPresence);
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

    private void Lost(int characterId, Action<bool?, DateTime> onPresence)
    {
        if (_running.TryRemove(characterId, out var cts))
            cts.Dispose();
        onPresence(null, DateTime.UtcNow);
    }

    public void Dispose()
    {
        foreach (var characterId in _running.Keys)
            Stop(characterId);
    }
}
