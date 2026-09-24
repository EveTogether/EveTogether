using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Reloads a server's fit-browser tab when another member shares or deletes a fit on that server. The events carry no
/// server-side id or timestamp for a share, so this reloads rather than patches. Reloads are merged per server: a client
/// with several characters on one server hears every change once per connection, and each reload fetches prices per row
/// and re-sorts, so a burst collapses into one reload after <see cref="Quiet"/>.
/// </summary>
public sealed class ServerFitLiveRefresh : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, Task> _reload;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly IDisposable _sharedSubscription;
    private readonly IDisposable _deletedSubscription;
    private readonly Dictionary<string, CancellationTokenSource> _pending = [];

    /// <param name="wait">How the quiet spell is waited out; a test replaces it to hold or release a round on purpose.</param>
    public ServerFitLiveRefresh(IEventBus bus, Func<string, Task> reload, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _reload = reload;
        _wait = wait ?? Task.Delay;
        _sharedSubscription = bus.Subscribe<FitSharedEvent>(evt => _Post(evt.SourceServerAddress));
        _deletedSubscription = bus.Subscribe<FitDeletedEvent>(evt => _Post(evt.SourceServerAddress));
    }

    /// <summary>Completed when no reload is waiting or running. Exposed so a test can await the round instead of racing it.</summary>
    public Task Settled { get; private set; } = Task.CompletedTask;

    private void _Post(string? serverAddress)
    {
        if (serverAddress is null)
            return;

        Dispatcher.UIThread.Post(() => _Schedule(serverAddress));
    }

    private void _Schedule(string serverAddress)
    {
        if (_pending.Remove(serverAddress, out CancellationTokenSource? superseded))
        {
            superseded.Cancel();
            superseded.Dispose();
        }

        CancellationTokenSource round = new();
        _pending[serverAddress] = round;
        Settled = _RunAsync(serverAddress, round);
    }

    private async Task _RunAsync(string serverAddress, CancellationTokenSource round)
    {
        try
        {
            await _wait(Quiet, round.Token);
            if (round.IsCancellationRequested)
                return;

            _pending.Remove(serverAddress);
            round.Dispose();
            await _reload(serverAddress);
        }
        catch (OperationCanceledException)
        {
            // superseded by a later change on the same server, which owns the reload
        }
    }

    public void Dispose()
    {
        _sharedSubscription.Dispose();
        _deletedSubscription.Dispose();
        foreach (CancellationTokenSource round in _pending.Values)
            round.Cancel();
        _pending.Clear();
    }
}
