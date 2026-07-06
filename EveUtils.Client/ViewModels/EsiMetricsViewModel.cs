using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The client ESI-metrics window: a live view of this client's per-bucket ESI call/error counters and
/// rate-limit headroom, read from the shared <see cref="IEsiRateLimitMonitor"/> (the data-layer counters live in
/// <c>EsiBucketState</c>, fed by the rate-limit handler after every call). The monitor
/// exposes no per-bucket change event, so the window polls on a light timer while open, alongside a manual Refresh.
/// Created per open and disposed on close so the timer only runs while the window is visible.
/// </summary>
public partial class EsiMetricsViewModel : ViewModelBase, IDisposable
{
    private readonly IEsiRateLimitMonitor? _monitor;
    private readonly ICharacterRegistry? _characters;
    private readonly ICharacterPortraitProvider? _portraits;
    private readonly DispatcherTimer? _timer;

    public ObservableCollection<EsiBucketRowViewModel> Buckets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    public bool IsEmpty => Count == 0;

    /// <summary>Design-time / fallback (no services).</summary>
    public EsiMetricsViewModel()
    {
    }

    public EsiMetricsViewModel(IEsiRateLimitMonitor monitor, ICharacterRegistry characters, ICharacterPortraitProvider portraits)
    {
        _monitor = monitor;
        _characters = characters;
        _portraits = portraits;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();
        Reload();
    }

    private void OnTick(object? sender, EventArgs e) => Reload();

    [RelayCommand]
    private void Refresh() => Reload();

    private void Reload()
    {
        if (_monitor is null)
        {
            Count = 0;
            return;
        }

        // Reconcile in place — update an existing row, add a new one, drop a vanished one — so the accordion's
        // expanded state (and scroll position) survive the poll instead of being rebuilt from scratch each tick.
        var live = _monitor.Buckets;
        var keys = live.Select(b => b.Key).ToHashSet();

        foreach (var bucket in live)
        {
            var existing = Buckets.FirstOrDefault(b => b.Key == bucket.Key);
            if (existing is null)
            {
                var row = new EsiBucketRowViewModel(bucket);
                Buckets.Add(row);
                if (row.IsCharacter)
                    _ = ResolveIdentityAsync(row); // once per new authed bucket, off the poll
            }
            else
                existing.Update(bucket);
        }

        for (var i = Buckets.Count - 1; i >= 0; i--)
            if (!keys.Contains(Buckets[i].Key))
                Buckets.RemoveAt(i);

        Count = Buckets.Count;
    }

    // Resolve an authed bucket's character identity once: name from the registry + portrait from the (opt-in)
    // portrait provider. Best-effort — a registry hiccup, images-off or offline leaves the "character:{id}" label and
    // the initial-glyph fallback. Marshals the result back to the UI thread to set the bound properties.
    private async Task ResolveIdentityAsync(EsiBucketRowViewModel row)
    {
        if (row.CharacterId is not { } characterId)
            return;

        string? name = null;
        if (_characters is not null)
        {
            try
            {
                var all = await _characters.GetAllAsync();
                name = all.FirstOrDefault(c => c.EsiCharacterId == characterId)?.Name;
            }
            catch
            {
                // registry hiccup — keep the id fallback
            }
        }

        Bitmap? portrait = null;
        if (_portraits is not null)
        {
            try
            {
                portrait = await _portraits.GetPortraitAsync(characterId, 64);
            }
            catch
            {
                // image off / offline / failure — keep the glyph fallback
            }
        }

        Dispatcher.UIThread.Post(() => row.ApplyCharacterIdentity(name, portrait));
    }

    /// <summary>True once disposed — the live poll timer is stopped (used to verify host teardown).</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        IsDisposed = true;
    }
}
