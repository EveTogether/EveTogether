using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
public sealed class EsiLocationMonitor(
    IEsiLocationClient locations,
    IToastService toasts,
    IServiceProvider services,
    ILogger<EsiLocationMonitor> logger) : IEsiLocationMonitor, ISingletonService, IDisposable
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

    // Who has already been warned about, per reason, and under which name. One warning per character per session:
    // the watch runs all evening, so without this the same toast would return every time the counter trips.
    private readonly Lock _warnGate = new();
    private readonly Dictionary<EsiErrorKind, Dictionary<int, string>> _warned = [];

    /// <summary>
    /// Starts watching <paramref name="characterId"/> for the rest of the session. <paramref name="onReading"/> is
    /// called after every reading; a reading without a system id means the watch was lost and no clock can be
    /// trusted. Watching a character that is already watched does nothing.
    /// </summary>
    public void Watch(int characterId, string characterName, Action<EsiLocationReading> onReading)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(characterId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => WatchAsync(characterId, characterName, onReading, cts.Token), CancellationToken.None);
    }

    public void Stop(int characterId)
    {
        if (!_running.TryRemove(characterId, out var cts))
            return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>The watch itself. <see cref="Watch"/> is only the fire-and-forget wrapper; tests drive this.</summary>
    internal async Task WatchAsync(int characterId, string characterName, Action<EsiLocationReading> onReading,
        CancellationToken cancellationToken)
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
                    Warn(characterId, characterName, result.Error?.Kind);
                    Lost(characterId, onReading);
                    return;
                }
                // A call our own gate withheld is not a failed read — nothing left the machine, so it is evidence of
                // nothing and the counter must not move. Counting it ended the watch every single day: the gate holds
                // the whole 11:00-11:03 UTC maintenance window and the budget is twenty polls at six seconds, so the
                // budget always ran out first. The abyssal clock and ET-63's location bootstrap went with it for the
                // rest of the session, which is what ET-81 reported. Same distinction EsiClient's outage detector
                // already makes, for the same reason.
                else if (result.Error?.Kind is not EsiErrorKind.Unavailable && ++failures > MaxConsecutiveFailures)
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

    // Only a refusal that the next poll cannot fix: no scope, and no working token. Real trouble that the next poll
    // might — 5xx, timeouts, rate limits — is transient and goes through the failure counter instead. ESI being down
    // is neither: the local gate answers those without sending anything, so they are not counted at all (ET-81).
    private static bool Fatal(EsiErrorKind? kind) => kind is EsiErrorKind.ScopeMissing or EsiErrorKind.AuthRequired;

    /// <summary>
    /// Reports that a character's location cannot be read, as one message covering everyone it applies to.
    /// </summary>
    /// <remarks>
    /// Grouped by reason rather than by character: three pilots without access is one problem, but "may not read"
    /// and "cannot sign in" are two, and they need different sentences. The card is re-shown under a fixed key as
    /// characters arrive, so a second pilot joins the message already on screen instead of raising a second card.
    /// </remarks>
    private void Warn(int characterId, string characterName, EsiErrorKind? kind)
    {
        logger.LogWarning("Location watch for {CharacterId} stopped: {Kind}.", characterId, kind);

        if (kind is not { } reason)
            return;

        (int Id, string Name)[] affected;
        lock (_warnGate)
        {
            if (!_warned.TryGetValue(reason, out var byId))
                _warned[reason] = byId = [];
            if (!byId.TryAdd(characterId, characterName))
                return;
            affected = [.. byId.Select(entry => (entry.Key, entry.Value))];
        }

        // What the location is used for is deliberately left out: this watch feeds whatever reads it, and naming
        // today's reader would age the moment a second one arrives.
        var (title, why, fix) = reason == EsiErrorKind.ScopeMissing
            ? ("No location access", $"EVE Together may not read the location of {Names(affected)}.", "Allow location")
            : ("ESI sign-in expired", $"EVE Together can no longer sign in as {Names(affected)}, so it cannot read "
                                      + "their location.", "Sign in again");

        // Actions keep the toast on screen until it is answered, which is the point: the one thing that fixes this is
        // a sign-in the pilot has to start.
        toasts.Show(title, why, ToastKind.Warning,
        [
            new ToastAction("Not now", () => { }),
            new ToastAction(fix, () => _ = GrantLocationAsync(affected), ToastActionStyle.Affirmative),
        ], onClosed: null, replacementKey: "location-access-" + reason);
    }

    private static string Names(IReadOnlyList<(int Id, string Name)> affected) => affected.Count switch
    {
        1 => affected[0].Name,
        _ => string.Join(", ", affected.Take(affected.Count - 1).Select(c => c.Name)) + " and " + affected[^1].Name,
    };

    /// <summary>
    /// Re-authenticates each affected character in turn, keeping the scopes already granted and adding location.
    /// </summary>
    /// <remarks>
    /// One at a time: each opens a sign-in the pilot has to complete, and starting them together would stack browser
    /// tabs and race the same login service.
    /// </remarks>
    private async Task GrantLocationAsync(IReadOnlyList<(int Id, string Name)> affected)
    {
        if (services.GetService<LocalEsiLoginService>() is not { } login)
            return;

        foreach (var character in affected)
        {
            try
            {
                await login.ReAuthenticateAsync(character.Id, [LocationScopeCatalog.ReadLocation]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Re-authentication for location access failed for {CharacterId}.", character.Id);
                toasts.Show("Sign-in failed", $"Could not add location access for {character.Name}. Try again from "
                                              + "that character's ESI menu.", ToastKind.Error);
            }
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
