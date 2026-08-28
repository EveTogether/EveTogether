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
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
// Avalonia's own Dispatcher (the UI thread) is all over this file; alias the CQRS one so neither name has to be
// spelled out at every call site.
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The free-standing fleet-metrics window: one live DPS graph per active member (reusing
/// <see cref="DpsViewModel"/>/<c>DpsGraph</c>, the same controls the ACTIVE header uses) plus the fleet roll-ups
/// (dealt + received DPS now, mining/bounty/neut reserved) via <see cref="FleetMetricCatalog.Aggregate"/>. Reads
/// live samples off the local bus for this fleet only; rows with no source yet show "—". Non-modal + disposable so
/// it keeps updating beside the main + fleets windows. <see cref="Layout"/> trades detail per member for members
/// per screen so the window stays readable as a fleet grows; the choice is remembered across sessions.
/// </summary>
public sealed partial class FleetMetricsViewModel : ObservableObject, IDisposable
{
    /// <summary>Where the chosen member layout is kept. One setting for the whole install, like the other shell
    /// preferences — the density that suits an FC does not change from fleet to fleet.</summary>
    public const string LayoutSettingKey = "ui.fleet-metrics.layout";

    private readonly long _fleetId;
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;
    private readonly IExternalCharacterLookup _lookup;
    private readonly DpsRenderDriver? _driver;
    private readonly IDialogService? _dialogs;
    private readonly Dictionary<int, DpsViewModel> _trackers = new();
    private readonly Dictionary<int, string> _nameById = new();
    private readonly List<IDisposable> _registrations = [];
    private int? _commanderCharacterId;
    private bool _layoutChosen;
    private bool _disposed;

    public FleetMetricsViewModel(IServiceProvider services, IFleetClient fleets, FleetInfo fleet)
    {
        var bus = services.GetRequiredService<IEventBus>();
        _services = services;
        _lookup = services.GetRequiredService<IExternalCharacterLookup>();
        _driver = services.GetRequiredService<DpsRenderDriver>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _fleetId = fleet.Id;
        FleetName = fleet.Name;

        _ = InitializeAsync(fleets);
        _ = LoadLayoutAsync();
        _subscription = bus.Subscribe<FleetMetricEvent>(OnFleetMetric);
    }

    public string FleetName { get; }
    public ObservableCollection<DpsViewModel> Members { get; } = [];

    [ObservableProperty] private string _dealtTotal = "—";
    [ObservableProperty] private string _receivedTotal = "—";
    [ObservableProperty] private string _miningTotal = "—";
    [ObservableProperty] private string _bountyTotal = "—";
    [ObservableProperty] private string _neutTotal = "—";

    /// <summary>The header badge: how many tracked members stand in the fleet commander's system. Lives in the
    /// header, so it stays on screen whichever member layout the screen shows.</summary>
    [ObservableProperty] private FleetCommanderPresence _commanderPresence = FleetCommanderPresence.Unknown;

    /// <summary>How the member rows are laid out. The view maps this onto an item template + panel; nothing else
    /// in this screen changes with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListLayout))]
    [NotifyPropertyChangedFor(nameof(IsGridLayout))]
    [NotifyPropertyChangedFor(nameof(IsCompactLayout))]
    [NotifyPropertyChangedFor(nameof(ShowsGraphs))]
    [NotifyPropertyChangedFor(nameof(LayoutHint))]
    private FleetMetricsLayout _layout = FleetMetricsLayout.List;

    public bool IsListLayout => Layout is FleetMetricsLayout.List;
    public bool IsGridLayout => Layout is FleetMetricsLayout.Grid;
    public bool IsCompactLayout => Layout is FleetMetricsLayout.Compact;

    /// <summary>Whether the member rows still carry a graph — the line legend is meaningless without one.</summary>
    public bool ShowsGraphs => Layout is not FleetMetricsLayout.Compact;

    /// <summary>Spells out what the current layout drops against the list, so trading detail for density is never a
    /// silent trade.</summary>
    public string LayoutHint => Layout switch
    {
        FleetMetricsLayout.Grid =>
            "Grid: name, DPS out/in, location and the live graph per card. The cap, neut and bounty figures show " +
            "in the list view.",
        FleetMetricsLayout.Compact =>
            "Compact: name, DPS out/in and location per line. Graphs and the cap, neut and bounty figures show " +
            "in the list view.",
        _ => "One live graph per active member; location shows when shared.",
    };

    /// <summary>Switch the member layout and remember it for the next session.</summary>
    [RelayCommand]
    private void SetLayout(FleetMetricsLayout layout)
    {
        _layoutChosen = true;
        if (layout == Layout)
            return;

        Layout = layout;
        _ = PersistLayoutAsync(layout);
    }

    private async System.Threading.Tasks.Task LoadLayoutAsync()
    {
        IReadOnlyList<SettingDto> settings;
        using (var scope = _services.CreateScope())
            settings = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());

        // Never set, or a value only a newer client knows — the List default already stands.
        if (!Enum.TryParse(settings.FirstOrDefault(s => s.Key == LayoutSettingKey)?.Value, ignoreCase: true,
                out FleetMetricsLayout stored))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // The stored layout lands asynchronously, so a click that beat it wins: restoring must never overwrite
            // the choice the user just made with the value that choice replaced.
            if (_disposed || _layoutChosen)
                return;
            Layout = stored;
        });
    }

    private async System.Threading.Tasks.Task PersistLayoutAsync(FleetMetricsLayout layout)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new SetSettingCommand(LayoutSettingKey, layout.ToString().ToLowerInvariant()));
    }

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

        // The FC is the roster's fleet-level commander — the same member the roster tree crowns.
        var commander = members.FirstOrDefault(m => m.WingId < 0 && m.Role == FleetRole.FleetCommander);

        // Mutate the cache + the Members collection on the UI thread, in lockstep with the sample router.
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            foreach (var character in connected)
                _nameById[character.CharacterId] = character.CharacterName;
            foreach (var member in members)
                Track(member.CharacterId);
            _commanderCharacterId = commander?.CharacterId;
            RefreshCommanderPresence();
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
        RefreshCommanderPresence();
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

    // Rides the sample stream the totals already ride, so the badge moves with the rest of the screen instead of
    // polling for locations of its own.
    private void RefreshCommanderPresence()
    {
        var commanderSystem = _commanderCharacterId is { } characterId && _trackers.TryGetValue(characterId, out var commander)
            ? commander.Location
            : null;
        CommanderPresence = FleetCommanderPresence.From(commanderSystem, _trackers.Values.Select(t => t.Location));
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
