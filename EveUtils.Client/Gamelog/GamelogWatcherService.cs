using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Gamelog;

/// <summary>
/// Tails the real EVE gamelog directory and feeds parsed combat into <see cref="GamelogClientService"/> — the
/// live replacement for the <see cref="SyntheticDpsFeeder"/>. The directory comes from the
/// <c>gamelog.directory</c> setting (configurable in the settings dialog), falling back to a platform
/// best-effort default (<see cref="GameLogLocations.Default"/>, Proton/Wine-aware on Linux).
///
/// Each parsed line carries the character's <c>Listener:</c> name, so the watcher attributes the hit to that
/// character — the coupling that makes a member's DPS its own. Hits are funnelled through a single-reader
/// channel so the (timer-driven, possibly overlapping) parse callbacks never touch a tracker concurrently
/// (<see cref="EveUtils.Shared.Modules.Gamelog.Aggregation.LiveDpsTracker"/> is single-owner).
/// </summary>
public sealed class GamelogWatcherService : ISingletonService
{
    /// <summary>Settings key for the user-configured gamelog directory.</summary>
    public const string GamelogDirectorySettingKey = "gamelog.directory";

    private readonly GamelogClientService _gamelog;
    private readonly IServiceProvider _services;
    private readonly ILogger<GamelogWatcherService> _logger;

    private readonly Channel<Parsed> _events = Channel.CreateUnbounded<Parsed>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();

    private GameLogWatcher? _watcher;
    private Task? _pump;
    private CancellationTokenSource? _pumpCts;

    public GamelogWatcherService(GamelogClientService gamelog, IServiceProvider services, ILogger<GamelogWatcherService> logger)
    {
        _gamelog = gamelog;
        _services = services;
        _logger = logger;
    }

    /// <summary>Raised with a character name the moment its gamelog is detected (UI: surface as local-only) and on
    /// every subsequent location change (jump/undock), so a row can refresh its system live.</summary>
    public event Action<string>? CharacterObserved;

    /// <summary>The directory currently being watched (null until started).</summary>
    public string? CurrentDirectory { get; private set; }

    /// <summary>Resolve the configured (or default) directory and begin tailing. Starts the hit pump once.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _pumpCts ??= new CancellationTokenSource();
        _pump ??= Task.Run(() => PumpAsync(_pumpCts.Token));

        var directory = await ResolveDirectoryAsync(cancellationToken);
        StartOn(directory);
    }

    /// <summary>Re-read the configured directory and re-baseline the watcher there (after a settings change).</summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default) =>
        StartOn(await ResolveDirectoryAsync(cancellationToken));

    private async Task<string> ResolveDirectoryAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery(), cancellationToken);
        var saved = settings.FirstOrDefault(s => s.Key == GamelogDirectorySettingKey)?.Value;
        return string.IsNullOrWhiteSpace(saved) ? GameLogLocations.Default() : saved;
    }

    private void StartOn(string directory)
    {
        lock (_gate)
        {
            _watcher?.Dispose();

            var watcher = new GameLogWatcher(directory);
            watcher.EventParsed += OnEventParsed;
            watcher.CharacterDetected += OnCharacterDetected;
            watcher.Start();

            _watcher = watcher;
            CurrentDirectory = directory;
        }
    }

    // All parsed events funnel through one channel so metric mutations happen on a single thread (the pump),
    // never concurrently with each other or with a UI snapshot read (CharacterMetrics is lock-guarded too).
    private void OnEventParsed(object? sender, GameLogEventArgs e) =>
        _events.Writer.TryWrite(new Parsed(e.CharacterName, e.LogEvent));

    private void OnCharacterDetected(object? sender, string characterName) =>
        CharacterObserved?.Invoke(characterName);

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
            {
                // One bad event (e.g. a failed remote publish to a coupled server) must never kill the pump —
                // that would silently freeze the entire live DPS feed after a single hit. Log and keep going.
                try
                {
                    switch (item.Event)
                    {
                        case CombatEvent c:
                            await _gamelog.AddHitAsync(item.Character, c.Direction, c.Amount, c.Target, c.Quality, c.Timestamp, cancellationToken);
                            break;
                        case BountyEvent b:
                            await _gamelog.AddBountyAsync(item.Character, b.Isk);
                            break;
                        case MiningEvent m:
                            await _gamelog.AddMiningAsync(item.Character, m);
                            break;
                        case RemoteRepEvent r:
                            _gamelog.AddRemoteRep(item.Character, r.Outgoing, r.Amount);
                            break;
                        case NeutEvent nu:
                            _gamelog.AddNeut(item.Character, nu.Outgoing, nu.Amount, nu.Timestamp);
                            break;
                        case CapTransferEvent ct:
                            _gamelog.AddCapTransfer(item.Character, ct.Outgoing, ct.Amount, ct.Timestamp);
                            break;
                        case LocationEvent l:
                            // SetLocation runs first (synchronously) so the snapshot is already populated when a
                            // CharacterObserved subscriber reads it — every jump refreshes the row, not just the
                            // first detection. Without this, only characters whose location was seeded by an earlier
                            // roster rebuild ever show a system.
                            _gamelog.SetLocation(item.Character, l.System);
                            CharacterObserved?.Invoke(item.Character);
                            break;
                        case NotifyEvent n:
                            _gamelog.AddNotify(item.Character, n.Timestamp, n.Message);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Gamelog pump: failed to process a {Event} for {Character}",
                        item.Event.GetType().Name, item.Character);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Stop tailing (the hit pump stops too). Safe to call when not started.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _pumpCts?.Cancel();
    }

    private readonly record struct Parsed(string Character, GameLogEvent Event);
}
