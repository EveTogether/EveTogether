using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The free-standing fleet-metrics window: one live DPS graph per active member (reusing
/// <see cref="DpsViewModel"/>/<c>DpsGraph</c>, the same controls the ACTIVE header uses) plus the fleet roll-ups
/// (dealt + received DPS now, mining/bounty/neut reserved) via <see cref="FleetMetricCatalog.Aggregate"/>. Reads
/// live samples off the local bus for this fleet only; rows with no source yet show "—". Non-modal + disposable so
/// it keeps updating beside the main + fleets windows.
/// </summary>
public sealed partial class FleetMetricsViewModel : ObservableObject, IDisposable
{
    private readonly long _fleetId;
    private readonly IDisposable _subscription;
    private readonly IExternalCharacterLookup _lookup;
    private readonly DpsRenderDriver? _driver;
    private readonly IDialogService? _dialogs;
    private readonly Dictionary<int, DpsViewModel> _trackers = new();
    private readonly Dictionary<int, string> _nameById = new();
    private readonly List<IDisposable> _registrations = [];
    private bool _disposed;

    public FleetMetricsViewModel(IServiceProvider services, IFleetClient fleets, FleetInfo fleet)
    {
        var bus = services.GetRequiredService<IEventBus>();
        _lookup = services.GetRequiredService<IExternalCharacterLookup>();
        _driver = services.GetRequiredService<DpsRenderDriver>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _fleetId = fleet.Id;
        FleetName = fleet.Name;

        _ = InitializeAsync(fleets);
        _subscription = bus.Subscribe<FleetMetricEvent>(OnFleetMetric);
    }

    public string FleetName { get; }
    public ObservableCollection<DpsViewModel> Members { get; } = [];

    [ObservableProperty] private string _dealtTotal = "—";
    [ObservableProperty] private string _receivedTotal = "—";
    [ObservableProperty] private string _miningTotal = "—";
    [ObservableProperty] private string _bountyTotal = "—";
    [ObservableProperty] private string _neutTotal = "—";

    // Warm the name cache AND pre-fill a row per roster member up front, so the window shows the whole fleet
    // deterministically instead of discovering members lazily one incoming sample at a time — which used to leave
    // members missing until they happened to publish (the "first only theirs, after reboot only mine, fills in after
    // clicking around" flakiness). Live data then fills each row as its samples arrive; a member with no live source
    // yet just shows "—".
    private async System.Threading.Tasks.Task InitializeAsync(IFleetClient fleets)
    {
        IReadOnlyList<ConnectedCharacterInfo> connected;
        IReadOnlyList<FleetMemberInfo> members;
        try
        {
            connected = await fleets.ListConnectedCharactersAsync();
            members = await fleets.ListMembersAsync(_fleetId);
        }
        catch
        {
            return; // transport hiccup — fall back to lazy discovery via incoming samples
        }

        // Mutate the cache + the Members collection on the UI thread, in lockstep with the sample router.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            foreach (var character in connected)
                _nameById[character.CharacterId] = character.CharacterName;
            foreach (var member in members)
                Track(member.CharacterId);
        });
    }

    private void OnFleetMetric(FleetMetricEvent integrationEvent) =>
        Dispatcher.UIThread.Post(() => RouteMetric(integrationEvent.Data));

    private void RouteMetric(MetricSample sample)
    {
        if (_disposed || sample.FleetId != _fleetId)
            return;

        switch (sample.Kind)
        {
            // Each live combat line arrives as its own kind; feed the matching series' target and let the shared
            // driver smooth toward it. The driver appends frames — never this method directly (one render path).
            case MetricKind.Dps or MetricKind.DpsIn or MetricKind.Neut or MetricKind.Cap:
                Track(sample.CharacterId).SetRate(sample.Kind, sample.Value);
                break;
            case MetricKind.Location:
                Track(sample.CharacterId).Location = sample.Text;
                break;
            case MetricKind.Bounty:
                Track(sample.CharacterId).Bounty = (long)sample.Value;
                break;
        }

        RefreshTotals();
    }

    // The one member row per character: created (graphed, name-resolved) on first sample of any kind, so DPS and
    // location land on the same row regardless of which arrives first.
    private DpsViewModel Track(int characterId)
    {
        if (_trackers.TryGetValue(characterId, out var tracker))
            return tracker;

        var known = _nameById.TryGetValue(characterId, out var resolved);
        tracker = new DpsViewModel(known ? resolved! : $"Char {characterId}", isSelf: false);
        _trackers[characterId] = tracker;
        Members.Add(tracker);

        // Render through the shared 30fps driver (the same path the own meters use), so a fleet graph scrolls,
        // smooths and decays identically instead of stepping at the 1 Hz sample rate. Disposed with the window.
        if (_driver is not null)
            _registrations.Add(_driver.Register(tracker));

        // A fleet member not coupled on this client is unknown to the connected-set warmup. Show the placeholder
        // now (samples arrive faster than a network call) and resolve the real name best-effort via public ESI —
        // the same lookup seam + day-cache the roster uses — then update the label. Runs once per id; later samples
        // reuse the existing tracker.
        if (!known)
            _ = ResolveNameAsync(characterId, tracker);

        return tracker;
    }

    /// <summary>Pop a fleet member's live DPS into the same borderless overlay the own meters use. It shares
    /// the tracker instance, so it renders through the shared driver with IN/OUT figures + markers like every graph.</summary>
    [RelayCommand]
    private void PopOut(DpsViewModel? tracker)
    {
        if (tracker is not null)
            _dialogs?.ShowDpsOverlay(tracker);
    }

    private async System.Threading.Tasks.Task ResolveNameAsync(int characterId, DpsViewModel tracker)
    {
        var info = await _lookup.LookupAsync(characterId);
        if (!info.Exists)
            return;

        // Back to the UI thread to mutate the cache + the observable label (the lookup continuation runs off-thread).
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            _nameById[characterId] = info.Name;
            tracker.Character = info.Name;
        });
    }

    private void RefreshTotals()
    {
        DealtTotal = Format(FleetMetricCatalog.Aggregate(MetricKind.Dps, _trackers.Values.Select(t => (double)t.Dealt)), "dps");
        ReceivedTotal = Format(FleetMetricCatalog.Aggregate(MetricKind.DpsIn, _trackers.Values.Select(t => (double)t.Received)), "dps");
        NeutTotal = Format(FleetMetricCatalog.Aggregate(MetricKind.Neut, _trackers.Values.Select(t => (double)t.Neut)), "GJ/s");

        var bounty = FleetMetricCatalog.Aggregate(MetricKind.Bounty, _trackers.Values.Select(t => (double)t.Bounty));
        BountyTotal = bounty is { } total ? DpsViewModel.CompactIsk((long)total) : "—";
        // Mining descriptor exists but has no live source yet — keep the "—" placeholder.
    }

    private static string Format(double? total, string unit) =>
        total is { } value ? $"{(long)value} {unit}" : "—";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
    }
}
