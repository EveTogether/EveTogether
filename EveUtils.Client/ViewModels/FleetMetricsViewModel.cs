using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
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
public sealed partial class FleetMetricsViewModel : ObservableObject, IDisposable, IRefreshableModule, IFleetOverlaySource
{
    /// <summary>Where the chosen member layout is kept. One setting for the whole install, like the other shell
    /// preferences — the density that suits an FC does not change from fleet to fleet.</summary>
    public const string LayoutSettingKey = "ui.fleet-metrics.layout";

    /// <summary>Where a fleet's dragged member order is kept, one key per fleet. Unlike the layout this is keyed per
    /// fleet: an order over character ids only means anything inside the fleet those characters are in, and one
    /// shared list would grow past the value limit as every fleet appended its own members to it.</summary>
    public const string OrderSettingKeyPrefix = "ui.fleet-metrics.order.";

    private const int MaxOrderValueLength = 4000;   // ClientSettingConfiguration caps a setting value at 4000

    /// <summary>How often the screen re-asks who has gone quiet. A second is far finer than the ninety-second window
    /// it is measuring against and costs one pass over the rows, so the transition lands on the row and in the badge
    /// within a tick of becoming true rather than waiting for a sample that is, by definition, not coming.</summary>
    public static readonly TimeSpan PresenceSweepInterval = TimeSpan.FromSeconds(1);

    private readonly long _fleetId;
    private readonly IFleetClient _fleets;
    private readonly IServiceProvider _services;
    private readonly IDisposable _subscription;
    private readonly IDisposable _rosterSubscription;
    private readonly IDisposable? _presenceSubscription;
    private readonly ILocalCharacterPresence? _presence;
    private readonly IExternalCharacterLookup _lookup;
    private readonly DpsRenderDriver? _driver;
    private readonly IDialogService? _dialogs;
    private readonly IToastService? _toasts;
    private readonly ISdeNameResolver _shipNames;
    private readonly Dictionary<int, DpsViewModel> _trackers = new();
    private readonly Dictionary<int, string> _nameById = new();

    // Keyed per character so one member's row can be torn down on its own when they are removed from the fleet.
    private readonly Dictionary<int, IDisposable> _registrations = [];

    // The roster facts behind each row (position, external, assigned fit) — what the member menu shows beyond the
    // live figures, and where the member id an actual removal needs comes from.
    private readonly Dictionary<int, FleetMemberInfo> _rosterByCharacter = [];

    // Every character a roster read has ever named on this screen. This is what separates the two ways a row can be
    // missing from the roster: a pilot it has NEVER named only ever arrived through a sample and is a straggler who
    // legitimately keeps their row (ET-46), while a pilot it named before and does not name now has been taken off —
    // a removal, which is an event and not an absence (ET-49).
    private readonly HashSet<int> _everOnRoster = [];

    // Characters this screen has seen removed. Their samples are dropped, so one still in flight — or a publisher
    // that has not caught up with the kick yet — cannot raise the row again a second later through lazy discovery.
    // Lifted the moment a roster read names them again: a pilot who rejoins is news, not an echo.
    private readonly HashSet<int> _removed = [];

    private readonly bool _isOwner;
    private readonly int _creatorCharacterId;
    private readonly long? _esiFleetId;
    private readonly int? _esiFleetBossId;

    private readonly DispatcherTimer _presenceSweep;

    private List<int> _storedOrder = [];
    private int? _commanderCharacterId;
    private bool _layoutChosen;
    private bool _disposed;

    /// <summary>This fleet's order key. Public so a test can read back what a drag stored.</summary>
    public string OrderSettingKey => OrderSettingKeyPrefix + _fleetId.ToString(CultureInfo.InvariantCulture);

    public FleetMetricsViewModel(IServiceProvider services, IFleetClient fleets, FleetInfo fleet, int actingCharacterId = 0)
    {
        var bus = services.GetRequiredService<IEventBus>();
        _services = services;
        _fleets = fleets;
        _lookup = services.GetRequiredService<IExternalCharacterLookup>();
        _driver = services.GetRequiredService<DpsRenderDriver>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _toasts = services.GetService<IToastService>();
        _shipNames = FitNameResolverFactory.For(services);
        _fleets = fleets;
        _fleetId = fleet.Id;
        FleetName = fleet.Name;

        // Who is looking. The removal action is the owner's alone — the server enforces that against the
        // authenticated character anyway, so this only keeps the option off a screen that could never use it.
        _creatorCharacterId = fleet.CreatorCharacterId;
        _isOwner = actingCharacterId != 0 && actingCharacterId == fleet.CreatorCharacterId;
        _esiFleetId = fleet.EsiFleetId;
        _esiFleetBossId = fleet.EsiFleetBossId;

        _ = InitializeAsync(fleets);
        _ = LoadLayoutAsync();
        _ = LoadOrderAsync();
        _subscription = bus.Subscribe<FleetMetricEvent>(OnFleetMetric);

        // A roster change made anywhere — this screen, the roster window, the fleet browser's card, another client —
        // arrives on the one shared watch (ET-52). It carries the client-only fleets too, which push no fleet.changed
        // and are precisely where a removal used to reach no other screen at all.
        _rosterSubscription = services.GetRequiredService<IFleetRosterWatch>().Subscribe(OnRosterChanged);

        // A pilot of ours logging in or out changes what their row may claim (ET-71): ESI keeps answering with the
        // spot they logged off at, which reads as a current position. One announcement, so the rows and the badge
        // move together instead of each noticing separately — the mistake behind ET-46, ET-49, ET-52 and ET-68.
        _presence = services.GetService<ILocalCharacterPresence>();
        _presenceSubscription = _presence?.Subscribe(ApplyPresence);
        ApplyPresence();

        // A fleet mate's client closing announces nothing, so the only evidence is the silence that follows it and
        // the only thing that can notice silence is a clock (ET-70).
        _presenceSweep = new DispatcherTimer { Interval = PresenceSweepInterval };
        _presenceSweep.Tick += (_, _) => RefreshPresence(DateTimeOffset.UtcNow);
        _presenceSweep.Start();
    }

    /// <summary>
    /// Re-reads the one presence verdict onto every row, then re-counts. Both halves come from the same call, so a
    /// member cannot be shown as offline while still sitting in the badge's denominator.
    /// </summary>
    private void ApplyPresence()
    {
        foreach (var tracker in _trackers.Values)
            ApplyPresence(tracker);

        RefreshCommanderPresence();
    }

    private void ApplyPresence(DpsViewModel tracker)
    {
        // null = not one of this client's characters, so nothing is claimed either way and the row goes on behaving
        // exactly as it always has. Only our own pilots can be called offline.
        var inGame = _presence?.IsInGame(tracker.CharacterId, tracker.Character);
        tracker.IsLocalCharacter = inGame is not null;
        tracker.InEve = inGame is true;
    }

    public string FleetName { get; }

    /// <summary>The fleet this screen reports on. Its module identity in the host is keyed on it, so metrics for a
    /// second fleet is a second module instead of a re-selection of the first one's screen.</summary>
    public long FleetId => _fleetId;

    public ObservableCollection<DpsViewModel> Members { get; } = [];

    // The fleet overlay reads these very rows rather than subscribing to the bus itself, so the pop-out and this
    // screen cannot disagree about who is where or who is taking what (ET-72).
    IReadOnlyList<DpsViewModel> IFleetOverlaySource.Members => Members;

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
            "Grid: every live figure plus the graph, per card. The bounty figure shows in the list view.",
        FleetMetricsLayout.Compact =>
            "Compact: every live figure on one line per member. Graphs and the bounty figure show in the list view.",
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

    /// <summary>
    /// Drop a dragged member in front of the member currently at <paramref name="insertionIndex"/> — the collection
    /// changes once, when the drag ends, not on every step of it. The drag gesture's one entry point: the view
    /// decides when and where, this decides what, so all three layouts reorder through the same code.
    /// </summary>
    public void MoveMemberTo(DpsViewModel dragged, int insertionIndex)
    {
        int from = Members.IndexOf(dragged);
        if (from < 0)
            return;

        // The insertion index counts the dragged member itself, which is about to leave its old place.
        int to = Math.Clamp(insertionIndex, 0, Members.Count);
        if (to > from)
            to--;
        if (to == from)
            return;

        Members.Move(from, to);
    }

    /// <summary>Remember the order as it now stands — called when a drag finishes, not on every step of it.</summary>
    public void CommitOrder()
    {
        _storedOrder = Members.Select(m => m.CharacterId).Where(id => id > 0).ToList();
        _ = PersistOrderAsync(_storedOrder);
    }

    private async System.Threading.Tasks.Task LoadOrderAsync()
    {
        IReadOnlyList<SettingDto> settings;
        using (var scope = _services.CreateScope())
            settings = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());

        List<int> stored = ParseOrder(settings.FirstOrDefault(s => s.Key == OrderSettingKey)?.Value);
        if (stored.Count == 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;

            // Rows can already be standing here — the roster pre-fill and the first samples do not wait for a
            // settings read — so adopting the order means re-sorting what is here, not just guiding what comes next.
            _storedOrder = stored;
            ApplyStoredOrder();
        });
    }

    /// <summary>Internal, not private, so a test can drive an order straight past <see cref="MaxOrderValueLength"/>
    /// without having to grow a fleet to 256+ live members first.</summary>
    internal async System.Threading.Tasks.Task PersistOrderAsync(IReadOnlyList<int> order)
    {
        string value = JoinOrder(order);
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new SetSettingCommand(OrderSettingKey, value));

        // JoinOrder silently drops what does not fit rather than failing the save — the fitting part is still worth
        // keeping — but a silent drop is exactly what ET-45 flagged: past this size the user would just find part of
        // their arrangement reset one day with no idea why.
        int kept = ParseOrder(value).Count;
        if (kept < order.Count)
            _toasts?.Show("Order not fully saved",
                $"Kept the manual order for the first {kept} of {order.Count} members; the rest stay in arrival order.",
                ToastKind.Warning);
    }

    // Stable: members the stored order knows keep its sequence, the rest keep the sequence they arrived in, behind.
    private void ApplyStoredOrder()
    {
        List<DpsViewModel> desired = Members
            .Select((member, index) => (Member: member, Rank: RankOf(member.CharacterId), Index: index))
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Member)
            .ToList();

        for (int target = 0; target < desired.Count; target++)
        {
            int current = Members.IndexOf(desired[target]);
            if (current != target)
                Members.Move(current, target);
        }
    }

    // A member the stored order names goes to its place among the members already standing there; anyone else joins
    // at the back, which is where an undragged fleet grows anyway.
    private void InsertInOrder(DpsViewModel tracker)
    {
        int rank = RankOf(tracker.CharacterId);
        if (rank == int.MaxValue)
        {
            Members.Add(tracker);
            return;
        }

        int index = 0;
        while (index < Members.Count && RankOf(Members[index].CharacterId) <= rank)
            index++;
        Members.Insert(index, tracker);
    }

    // Unranked sorts last, which is also what happens to a stored id whose character has left the fleet: it simply
    // never matches a row, so it costs nothing and leaves no gap.
    private int RankOf(int characterId)
    {
        int rank = _storedOrder.IndexOf(characterId);
        return rank < 0 ? int.MaxValue : rank;
    }

    internal static List<int> ParseOrder(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0)
                .Where(id => id > 0)
                .ToList();

    /// <summary>
    /// The stored order as one setting value. A setting value holds 4000 characters and a character id plus its
    /// comma is at most eleven, so this covers every member of a full 256-member fleet (EVE's own hard cap,
    /// <see cref="EveUtils.Shared.Modules.Fleet.FleetStructureLimits.MaxFleetSize"/>) with room to spare. If a
    /// caller still manages to overflow it, the tail is dropped rather than truncated mid-id — and the tail is the
    /// part nobody dragged, since new members join at the back and stay there until they are moved.
    /// <see cref="PersistOrderAsync"/> is what tells the user when that happened.
    /// </summary>
    internal static string JoinOrder(IEnumerable<int> order)
    {
        var value = new StringBuilder();
        foreach (int characterId in order)
        {
            string next = value.Length == 0
                ? characterId.ToString(CultureInfo.InvariantCulture)
                : "," + characterId.ToString(CultureInfo.InvariantCulture);
            if (value.Length + next.Length > MaxOrderValueLength)
                break;
            value.Append(next);
        }

        return value.ToString();
    }

    /// <summary>
    /// Re-read the roster because the user opened this screen again. The pre-fill is the ONLY thing that can put a
    /// member here who publishes nothing — an external pilot has no client of their own, so no sample of theirs is
    /// ever coming — and it used to run once, at construction. Someone who joined after that stayed off the screen,
    /// and with it out of the roll-up totals and the WITH FC badge's denominator (ET-46).
    ///
    /// Additive for anyone the roster has never named: rows legitimately come from samples alone (a straggler who is
    /// in the fleet in-game but not on the roster), and dropping those on every re-open would lose live data the FC is
    /// watching. A member the roster HAS named and no longer names is the other case entirely — they were taken off
    /// the fleet, so their row goes with them (ET-49).
    /// </summary>
    public void RefreshModule() => _ = InitializeAsync(_fleets);

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

            // Additive, per ET-46's RefreshModule contract: a re-read refreshes the facts behind the rows that are
            // here and adds the members it did not know. A row this read does not name is not necessarily gone — it
            // may be a straggler who only ever arrived through a sample.
            _rosterByCharacter.Clear();
            foreach (var member in members)
            {
                _rosterByCharacter[member.CharacterId] = member;
                _everOnRoster.Add(member.CharacterId);
                _removed.Remove(member.CharacterId);   // named again: they are back, so nothing about them is stale
                // The server's account of when this pilot last published, which a screen that just opened cannot have
                // heard for itself — without it every member would read as never-heard-from for the first sweep or two
                // and a pilot who left hours ago would be indistinguishable from one who shares nothing (ET-70).
                Track(member.CharacterId).ServerLastSeenAt = member.LastSeenAt;
            }

            // …and the one thing a re-read does take away (ET-49). A pilot this roster HAS named before and does not
            // name now has been removed from the fleet, wherever that removal was made — the roster screen, another
            // client, this screen a moment ago. That is a fact about them, not a gap in this read, so their row goes.
            // A pilot the roster has never named is untouched: nothing removed them, they were simply never on it.
            foreach (int characterId in _everOnRoster.Where(id => !_rosterByCharacter.ContainsKey(id)).ToList())
                _DropRow(characterId);

            _commanderCharacterId = commander?.CharacterId;
            RefreshPresence(DateTimeOffset.UtcNow);
        });
    }

    /// <summary>
    /// Re-read who has gone quiet, then re-count. Public and clock-driven so a test drives it with a time it owns
    /// instead of waiting ninety seconds; the window runs it on <see cref="PresenceSweepInterval"/>.
    ///
    /// It is a sweep and not a consequence of an arriving sample for one reason: the moment a pilot becomes silent is
    /// the moment nothing arrives. A fleet that has gone entirely quiet — everyone logged for the night — publishes
    /// nothing at all, so a screen that only recomputed on incoming samples would freeze with everybody still shown
    /// as present, which is exactly the state this ticket exists to stop showing.
    /// </summary>
    public void RefreshPresence(DateTimeOffset now)
    {
        foreach (var tracker in _trackers.Values)
            tracker.RefreshPresence(now);

        RefreshCommanderPresence();
    }

    // A roster change reaches a screen that is already standing open, where ET-46's RefreshModule only fires when the
    // user opens the module again. A removal is acted on directly as well as re-read: the row goes the moment the
    // removal is announced, without waiting on a transport round trip, which is what keeps the card from being on
    // screen for the instant a late sample could still raise it (ET-49). Everything else is the additive re-read.
    private void OnRosterChanged(FleetRosterChange change)
    {
        if (_disposed || change.FleetId != _fleetId)
            return;

        if (change.Kind is FleetRosterChangeKind.MemberRemoved)
            _DropRow(change.CharacterId);

        RefreshModule();
    }

    private void OnFleetMetric(FleetMetricEvent integrationEvent) =>
        Dispatcher.UIThread.Post(() => RouteMetric(integrationEvent.Data));

    private void RouteMetric(MetricSample sample)
    {
        // A sample for a pilot this screen has seen removed is an echo of a fleet they are no longer in: it may not
        // raise their row again through lazy discovery below, and it may not count towards the fleet's totals either.
        if (_disposed || sample.FleetId != _fleetId || _removed.Contains(sample.CharacterId))
            return;

        switch (sample.Kind)
        {
            // Each live combat line arrives as its own kind; feed the matching series' target and let the shared
            // driver smooth toward it. The driver appends frames — never this method directly (one render path).
            case MetricKind.Dps or MetricKind.DpsIn or MetricKind.Neut or MetricKind.Cap or MetricKind.NeutIn:
                Track(sample.CharacterId).SetRate(sample.Kind, sample.Value);
                break;
            case MetricKind.Location:
                var row = Track(sample.CharacterId);
                row.Location = sample.Text;
                // The anchor is stamped on the sender's clock; re-base it onto ours before it feeds a countdown.
                row.AbyssalAnchorUtc = AbyssalSpace.AnchorFromWire(sample.AbyssalAnchorMs, sample.UnixMs, DateTime.UtcNow);
                row.RefreshLocationDisplay(); // 1 Hz path: the countdown moves even when system and anchor do not
                break;
            case MetricKind.Bounty:
                Track(sample.CharacterId).Bounty = (long)sample.Value;
                break;
            case MetricKind.Presence:
                // What the pilot's own client says about their game (ET-70). An out-of-range value is a newer
                // client's state we have no reading for, and claiming nothing is the safe answer to that.
                Track(sample.CharacterId).ReportedPresence =
                    Enum.IsDefined((PresenceState)(int)sample.Value) ? (PresenceState)(int)sample.Value : PresenceState.Unknown;
                break;
        }

        // Stamped after the switch, on the row a handled sample just created or updated: "when did we last hear from
        // this pilot" rides the same kinds the screen already draws, so a kind it ignores raises no row of its own.
        if (_trackers.TryGetValue(sample.CharacterId, out var tracker))
        {
            tracker.LastSampleAt = DateTimeOffset.UtcNow;
            // A sample arriving ends any silence on the spot, rather than at the next sweep a second later. That
            // second matters: a pilot coming back must not still read as gone while their figures are already moving.
            tracker.IsSilent = false;
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
        tracker = new DpsViewModel(known ? resolved! : $"Char {characterId}", isSelf: false)
        {
            CharacterId = characterId,
        };
        _trackers[characterId] = tracker;

        // The verdict travels with the row from the moment it exists, so a row created between two announcements
        // does not briefly claim a location for a pilot who is not in game.
        ApplyPresence(tracker);
        InsertInOrder(tracker);

        // Render through the shared 30fps driver (the same path the own meters use), so a fleet graph scrolls,
        // smooths and decays identically instead of stepping at the 1 Hz sample rate. Disposed with the window.
        if (_driver is not null)
            _registrations[characterId] = _driver.Register(tracker);

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

    /// <summary>
    /// Pop the whole fleet out (ET-72): the WITH FC ratio plus who is taking the most damage and who is being neuted
    /// the most, in the same borderless overlay the per-character meters use. It is handed this view-model rather
    /// than a copy of its figures — the same reason the DPS pop-out is handed the tracker instance.
    /// </summary>
    [RelayCommand]
    private void PopOutFleet() => _dialogs?.ShowFleetOverlay(new FleetOverlayViewModel(this));

    // --- The shared member menu (ET-44) ---

    /// <summary>
    /// Rebuilds one member's right-click menu with the facts as they stand right now. Called by the view the moment
    /// the menu is requested, on the one member ItemsControl, so all three densities get a current "last update"
    /// line without 40 rows re-rendering a relative time every second.
    /// </summary>
    public void RefreshMemberMenu(DpsViewModel tracker) =>
        tracker.MemberMenu = FleetMemberMenu.Build(_FactsFor(tracker), DateTimeOffset.UtcNow, _RemoveCommandFor(tracker));

    private FleetMemberFacts _FactsFor(DpsViewModel tracker)
    {
        var member = _rosterByCharacter.GetValueOrDefault(tracker.CharacterId);
        var fit = member?.AssignedFit;
        return new FleetMemberFacts(
            tracker.Character,
            member?.Role ?? FleetRole.Unassigned,
            member?.IsExternal ?? false,
            ShipName: fit is null ? null : _shipNames.TypeName(fit.ShipTypeId),
            FitName: fit?.FitName,
            Location: tracker.Location,
            IsWithCommander: tracker.IsWithCommander,
            LastSampleAt: tracker.LastHeardAt,
            TracksLiveMetrics: true,
            Presence: tracker.Presence);
    }

    // No member id means no roster row to remove (a pilot seen only through samples), and the creator can never be
    // removed — ownership has to move first, so offering it would only ever produce the server's refusal.
    private IRelayCommand? _RemoveCommandFor(DpsViewModel tracker) =>
        _isOwner
        && _rosterByCharacter.TryGetValue(tracker.CharacterId, out var member)
        && member.CharacterId != _creatorCharacterId
            ? new AsyncRelayCommand(() => RemoveMemberAsync(tracker, member))
            : null;

    /// <summary>
    /// Removes one member: out of the EVE Together fleet, and only then — and only when this fleet is coupled to an
    /// in-game one — the separate question whether to kick them in-game too. The whole flow lives in
    /// <see cref="FleetMemberRemovalService"/> so this screen and the roster ask it in exactly the same way, and the
    /// card goes because that service announces the removal on <see cref="IFleetRosterWatch"/> — the same route by
    /// which a removal made on the roster or the browser card reaches this screen. Dropping the row here as well
    /// would be the fourth private hand-off between two screens, which is the thing ET-52 is about.
    /// </summary>
    private async System.Threading.Tasks.Task RemoveMemberAsync(DpsViewModel tracker, FleetMemberInfo member)
    {
        if (_services.GetService<FleetMemberRemovalService>() is not { } removal)
            return;

        var (status, message) = await removal.RemoveAsync(_fleets, new FleetMemberRemovalRequest(
            _fleetId, member.Id, member.CharacterId, tracker.Character, FleetName, _esiFleetId, _esiFleetBossId));

        if (status is FleetMemberRemovalStatus.Cancelled)
            return;

        _toasts?.Show("Remove from fleet", message, status switch
        {
            FleetMemberRemovalStatus.Failed => ToastKind.Error,
            // Off the roster but still in the in-game fleet is not what the FC asked for — say so loudly.
            FleetMemberRemovalStatus.RemovedFromFleetInGameFailed => ToastKind.Warning,
            _ => ToastKind.Success
        });
    }

    // Tears a member's row down completely: the collection, the tracker cache, the roster facts and the render
    // registration — a graph left registered would keep being driven for a pilot who is no longer in the fleet.
    // Re-committing the order afterwards is what keeps the stored drag order free of the departed id (ET-28).
    private void _DropRow(int characterId)
    {
        // Recorded before anything else, and whether or not there is a row to tear down: this is the "they were
        // removed" fact the sample router reads, and a removal confirmed before the pilot's first sample arrived
        // still has to keep that sample from raising a row.
        _removed.Add(characterId);

        if (!_trackers.Remove(characterId, out var tracker))
            return;

        Members.Remove(tracker);
        _rosterByCharacter.Remove(characterId);
        if (_registrations.Remove(characterId, out var registration))
            registration.Dispose();

        // The pop-out is a window of its own onto this very tracker, so it is a screen showing the removed pilot like
        // any other and closes with their row (ET-52). Left open it would stand there for good: its graph is fed by
        // samples this screen now drops, so it can only ever show the last frame from before the removal.
        _dialogs?.CloseDpsOverlay(tracker);

        CommitOrder();
        RefreshTotals();
        RefreshCommanderPresence();
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

            // The presence verdict is matched on the name as well as the id — window titles are the only evidence
            // on Windows, and the launcher's id argument is absent on a manual login. Until this lands the row was
            // called "Char 90250177", which matches no running client, so it has to be asked again now that the
            // pilot has a name. Without this a row briefly reads "offline" for someone who is plainly flying.
            ApplyPresence(tracker);
            RefreshCommanderPresence();
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
        // KnownLocation, not Location: a pilot of ours who is not in game has no location anyone may act on, so
        // they fall out of the denominator instead of counting as "somewhere else" (ET-63/ET-71). It is the same
        // property their row shows, so the count and the screen cannot tell different stories.
        var commanderSystem = _commanderCharacterId is { } characterId && _trackers.TryGetValue(characterId, out var commander)
            ? commander.KnownLocation
            : null;
        CommanderPresence = FleetCommanderPresence.From(
            commanderSystem, _trackers.Values.Select(t => new FleetMemberStanding(t.KnownLocation, t.IsOffline)));

        // Colour each member's own location off the badge that was just computed, rather than comparing systems a
        // second time — one verdict, so a green row and the badge's ratio can never disagree. The commander's own row
        // turns green too: they are a member, and they are trivially in their own system, exactly as the ratio counts
        // them.
        foreach (var tracker in _trackers.Values)
            tracker.IsWithCommander = CommanderPresence.IsWith(tracker.KnownLocation);
    }

    private static string Format(double? total, string unit) =>
        total is { } value ? $"{(long)value} {unit}" : "—";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The fleet overlay is a window onto THIS view-model's rows, so it goes when they do (the ET-52 rule the DPS
        // pop-out follows on a dropped row). Left open it would sit on top of the game showing the last frame before
        // the screen closed, with nothing left to update it — the most convincing kind of stale.
        _dialogs?.CloseFleetOverlay(_fleetId);

        _presenceSweep.Stop();
        _subscription.Dispose();
        _rosterSubscription.Dispose();
        _presenceSubscription?.Dispose();
        foreach (var registration in _registrations.Values)
            registration.Dispose();
        _registrations.Clear();
    }
}
