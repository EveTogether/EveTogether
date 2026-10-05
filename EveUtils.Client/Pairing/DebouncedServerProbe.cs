using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace EveUtils.Client.Pairing;

/// <summary>
/// Asks a server address who it is once typing settles: debounced, superseded by a newer address, five seconds per
/// attempt. Display-only — the probe accepts any certificate; real trust is established via TOFU at pairing. Shared by
/// the couple dialog and the setup wizard so both answer the same way.
/// </summary>
public sealed class DebouncedServerProbe<TResult> : IDisposable where TResult : class
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, Task<TResult?>> _probe;
    private readonly Action _onChecking;
    private readonly Action _onCleared;
    private readonly Action<string, TResult?> _onResult;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _probeCts;
    private string? _pendingAddress;

    /// <param name="onResult">The address probed and what it answered; null when it did not answer in time.</param>
    public DebouncedServerProbe(Func<string, CancellationToken, Task<TResult?>> probe,
        Action onChecking, Action onCleared, Action<string, TResult?> onResult)
    {
        _probe = probe;
        _onChecking = onChecking;
        _onCleared = onCleared;
        _onResult = onResult;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = ProbeNowAsync(_pendingAddress);
        };
    }

    /// <summary>Restarts the wait: only the last keystroke probes.</summary>
    public void AddressChanged(string? address)
    {
        _pendingAddress = address;
        _onChecking();
        _debounce.Stop();
        _debounce.Start();
    }

    public async Task ProbeNowAsync(string? address)
    {
        _debounce.Stop();
        var trimmed = address?.Trim();
        _probeCts?.Cancel();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            _onCleared();
            return;
        }

        _probeCts = new CancellationTokenSource(ProbeWindow);
        var ct = _probeCts.Token;
        _onChecking();
        TResult? result;
        try
        {
            result = await _probe(trimmed, ct);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) return;
            result = null;
        }
        catch (Exception)
        {
            result = null;
        }

        if (_probeCts?.Token != ct) return; // a newer probe superseded this one
        _onResult(trimmed, result);
    }

    public void Dispose()
    {
        _debounce.Stop();
        _probeCts?.Cancel();
    }
}
