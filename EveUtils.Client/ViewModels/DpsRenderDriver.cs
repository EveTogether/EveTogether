using System;
using System.Collections.Generic;
using Avalonia.Threading;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The single ~30fps driver for every DPS graph — the local meters (main window + popped-out overlay) and the
/// fleet-member meters alike. Each registered <see cref="DpsViewModel"/> gets one <see cref="DpsViewModel.RenderFrame"/>
/// per tick on the UI thread, so all graphs scroll, smooth and decay through the exact same path: a change to
/// the render cadence or smoothing lands everywhere at once instead of drifting between the own and fleet graphs.
/// </summary>
public sealed class DpsRenderDriver : ISingletonService
{
    private const double FrameIntervalMs = 33; // ~30fps

    private readonly List<DpsViewModel> _trackers = [];
    private DispatcherTimer? _timer;

    /// <summary>Drive <paramref name="tracker"/> every frame until the returned handle is disposed. The own meters
    /// live for the app lifetime (never disposed); a fleet-metrics window disposes its handles when it closes.</summary>
    public IDisposable Register(DpsViewModel tracker)
    {
        EnsureTimer();
        if (!_trackers.Contains(tracker))
            _trackers.Add(tracker);
        return new Registration(this, tracker);
    }

    // Created lazily on the first registration — always on the UI thread (windows register from their view model),
    // unlike DI construction which may run off-thread.
    private void EnsureTimer()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // UI-thread, single-threaded: an index loop tolerates a register/unregister landing between frames (never during).
    private void Tick()
    {
        for (var i = 0; i < _trackers.Count; i++)
            _trackers[i].RenderFrame();
    }

    private void Unregister(DpsViewModel tracker) => _trackers.Remove(tracker);

    private sealed class Registration(DpsRenderDriver driver, DpsViewModel tracker) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            driver.Unregister(tracker);
        }
    }
}
