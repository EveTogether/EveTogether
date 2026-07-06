using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The home dashboard (replaces the old global "live DPS / fleet" landing, which broadcast every connected client's
/// DPS even outside your fleet). Shows ONLY your own data: your own characters' live DPS (the self trackers, never the
/// remote/global ones), the fleets you own or fly in, the latest fits shared on your coupled servers, and recent
/// inbox activity. Every source is best-effort — a server that is down or a missing service just leaves its card empty
/// rather than failing the home.
/// </summary>
public sealed partial class HomeDashboardViewModel : ObservableObject
{
    private readonly ObservableCollection<DpsViewModel> _allTrackers;
    private readonly IClientSessionStore? _sessions;
    private readonly IServerRegistry? _serverRegistry;
    private readonly FleetClient? _fleets;
    private readonly ServerFitShareClient? _fitShare;
    private readonly ICharacterRegistry? _registry;
    private readonly EveClientPresenceService? _presence;
    private readonly IRemoteBusConnector? _busConnector;
    private readonly ICharacterPortraitProvider? _portraits;
    private readonly ISdeNameResolver? _sdeNames;
    private readonly ITypeImageProvider? _typeImages;
    private readonly GamelogClientService? _gamelog;     // per-character location, even without combat (jump/undock)
    private readonly GamelogWatcherService? _watcher;    // raises CharacterObserved on every parsed line (incl. jumps)

    /// <summary>Design-time / fallback constructor (no services).</summary>
    public HomeDashboardViewModel()
    {
        _allTrackers = [];
        MyCharacters = [];
    }

    public HomeDashboardViewModel(IServiceProvider services, ObservableCollection<DpsViewModel> dpsTrackers)
    {
        _allTrackers = dpsTrackers;
        _sessions = services.GetService<IClientSessionStore>();
        _serverRegistry = services.GetService<IServerRegistry>();
        _fleets = services.GetService<FleetClient>();
        _fitShare = services.GetService<ServerFitShareClient>();
        _registry = services.GetService<ICharacterRegistry>();
        _presence = services.GetService<EveClientPresenceService>();
        _busConnector = services.GetService<IRemoteBusConnector>();
        _portraits = services.GetService<ICharacterPortraitProvider>();
        _sdeNames = services.GetService<ISdeNameResolver>();
        _typeImages = services.GetService<ITypeImageProvider>();
        _gamelog = services.GetService<GamelogClientService>();
        _watcher = services.GetService<GamelogWatcherService>();

        MyCharacters = new ObservableCollection<DpsViewModel>(dpsTrackers.Where(t => t.IsSelf));
        dpsTrackers.CollectionChanged += OnTrackersChanged;

        if (_busConnector is not null)
        {
            _busConnector.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateServerState);
            UpdateServerState();
        }

        // Live presence: rebuild the roster (count, idle/online split, dots) the moment an EVE client starts/stops,
        // instead of only on a manual refresh (the running-client probe raises this on change).
        if (_presence is not null)
            _presence.Changed += evidence => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RebuildRosterAsync());

        // Event-driven auto-refresh of the fleets + fits cards (the roster/presence are already live): a fleet
        // lifecycle/roster change reloads the fleets card, a freshly shared fit reloads the fits card — so they no
        // longer wait for the next manual REFRESH. The bus holds the handler; Home lives for the app session.
        var bus = services.GetService<IEventBus>();
        if (bus is not null)
        {
            bus.Subscribe<FleetChangedEvent>(evt => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadFleetsAsync()));
            bus.Subscribe<FitSharedEvent>(evt => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadFitsAsync()));
        }

        // Live location for online characters (even without combat): every parsed gamelog line — including a jump —
        // raises CharacterObserved, so we refresh that character's location from the gamelog snapshot.
        if (_watcher is not null)
            _watcher.CharacterObserved += name => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnCharacterObserved(name));
    }

    private void OnCharacterObserved(string name)
    {
        var row = MyCharacters.FirstOrDefault(c => string.Equals(c.Character, name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            _ = RebuildRosterAsync(); // a character we don't list yet became active → fold it in
            return;
        }
        if (_gamelog is not null && _gamelog.Snapshot(name).Location is { } location && !string.IsNullOrWhiteSpace(location))
            row.Location = location;
    }

    /// <summary>Your own characters with live DPS — the self trackers only. The home no longer shows the DPS of every
    /// other connected client; remote members live in the fleet metrics window, scoped to a fleet you're in.</summary>
    public ObservableCollection<DpsViewModel> MyCharacters { get; }

    /// <summary>The fleets you own or fly in that are still active (formed) — newest activation first.</summary>
    public ObservableCollection<DashboardFleetViewModel> Fleets { get; } = [];

    /// <summary>The most recent fits shared on your coupled servers (newest first).</summary>
    public ObservableCollection<DashboardFitViewModel> LatestFits { get; } = [];

    [ObservableProperty] private int _charactersTotal;
    [ObservableProperty] private int _charactersInEve;
    [ObservableProperty] private int _activeFleetCount;
    [ObservableProperty] private int _formingFleetCount;
    [ObservableProperty] private int _sharedFitCount;

    /// <summary>True when at least one coupled server's bus is connected — drives the SERVER tile.</summary>
    [ObservableProperty] private bool _isServerConnected;
    [ObservableProperty] private string _serverStatusLabel = "Disconnected";

    /// <summary>Your characters' combined session bounty, compact ("894.4k"). Session-only (resets on restart);
    /// a true calendar-day total is a later refinement.</summary>
    [ObservableProperty] private string _iskTodayText = "0";

    public bool HasCharacters => MyCharacters.Count > 0;
    public bool HasFleets => Fleets.Count > 0;
    public bool HasFits => LatestFits.Count > 0;

    private void OnTrackersChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = RebuildRosterAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        UpdateServerState();
        await RebuildRosterAsync();
        await LoadFleetsAsync();
        await LoadFitsAsync();
    }

    /// <summary>Rebuilds the "Your characters" roster: every live self tracker (with its running graph) plus a greyed
    /// idle placeholder for each of your other characters that has no live combat yet (decision 2026-06-17). The
    /// active-tracker reconciliation runs synchronously (before any await) so it stays correct without a registry;
    /// idle placeholders + the "in EVE" count + presence/portraits need the registry and are merged after.</summary>
    public async Task RebuildRosterAsync()
    {
        var actives = _allTrackers.Where(t => t.IsSelf).ToList();
        var activeNames = new HashSet<string>(actives.Select(a => a.Character), StringComparer.OrdinalIgnoreCase);

        // Drop vanished live trackers and any idle placeholder whose character just came online; add missing actives.
        for (var i = MyCharacters.Count - 1; i >= 0; i--)
        {
            var entry = MyCharacters[i];
            var isLiveTracker = _allTrackers.Contains(entry);
            if (isLiveTracker ? !actives.Contains(entry) : activeNames.Contains(entry.Character))
                MyCharacters.RemoveAt(i);
        }
        foreach (var tracker in actives)
            if (!MyCharacters.Contains(tracker))
                MyCharacters.Add(tracker);

        OnPropertyChanged(nameof(HasCharacters));
        UpdateIskToday();

        if (_registry is null)
            return; // no registry (design-time / tests) → live trackers only, no idle merge

        var characters = await _registry.GetAllAsync();
        var evidence = _presence?.Current;
        CharactersTotal = characters.Count;
        CharactersInEve = evidence is null ? 0 : characters.Count(c => evidence.Matches(c.Name, c.EsiCharacterId ?? 0));

        // Idle = my characters (registry) with no live tracker → a greyed placeholder row.
        var idle = characters.Where(c => c.EsiCharacterId is not null && !activeNames.Contains(c.Name)).ToList();
        var idleNames = new HashSet<string>(idle.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = MyCharacters.Count - 1; i >= 0; i--)
        {
            var entry = MyCharacters[i];
            if (!_allTrackers.Contains(entry) && !idleNames.Contains(entry.Character))
                MyCharacters.RemoveAt(i); // idle placeholder no longer wanted
        }
        foreach (var character in idle)
            if (!MyCharacters.Any(e => string.Equals(e.Character, character.Name, StringComparison.OrdinalIgnoreCase)))
                MyCharacters.Add(new DpsViewModel(character.Name, isSelf: true) { IsLive = false });

        OnPropertyChanged(nameof(HasCharacters));
        await LoadCharacterPresenceAsync();
    }

    private void UpdateServerState()
    {
        var connected = _busConnector is not null &&
                        _busConnector.States.Values.Any(s => s == ServerConnectionState.Connected);
        IsServerConnected = connected;
        ServerStatusLabel = connected ? "Connected" : "Disconnected";
    }

    private void UpdateIskToday()
    {
        long total = 0;
        foreach (var tracker in MyCharacters)
            total += tracker.Bounty;
        IskTodayText = CompactIsk(total);
    }

    /// <summary>Sets each character row's "in EVE" presence dot and loads its ESI portrait (best-effort, opt-in
    /// images). Presence comes from the running-client probe; the portrait from the character's resolved id.</summary>
    private async Task LoadCharacterPresenceAsync()
    {
        if (_registry is null)
            return;

        var characters = await _registry.GetAllAsync();
        var evidence = _presence?.Current;
        foreach (var tracker in MyCharacters)
        {
            var match = characters.FirstOrDefault(c => string.Equals(c.Name, tracker.Character, StringComparison.OrdinalIgnoreCase));
            var id = match?.EsiCharacterId ?? 0;
            tracker.InEve = evidence is not null && evidence.Matches(tracker.Character, id);
            if (_portraits is not null && id > 0 && tracker.Portrait is null)
                tracker.Portrait = await _portraits.GetPortraitAsync(id, 64);
            // Location from the gamelog (jump/undock), so an online character shows its system even without combat.
            if (_gamelog is not null && _gamelog.Snapshot(tracker.Character).Location is { } location && !string.IsNullOrWhiteSpace(location))
                tracker.Location = location;
        }
    }

    private async Task LoadFleetsAsync()
    {
        Fleets.Clear();
        if (_fleets is null || _sessions is null)
        {
            ActiveFleetCount = FormingFleetCount = 0;
            OnPropertyChanged(nameof(HasFleets));
            return;
        }

        var seen = new HashSet<long>();
        var built = new List<DashboardFleetViewModel>();
        foreach (var server in await _sessions.ListServersAsync())
        {
            var serverName = _serverRegistry is null ? server : await _serverRegistry.DisplayNameAsync(server);
            IReadOnlyList<ClientSessionTokens> sessions;
            try { sessions = await _sessions.LoadAllAsync(server); }
            catch { continue; }

            foreach (var session in sessions)
            {
                IReadOnlyList<FleetInfo> fleets;
                try { fleets = await _fleets.ListMyFleetsAsync(server, session.CharacterId); }
                catch { continue; }

                foreach (var fleet in fleets.Where(f => f.State == FleetState.Active && seen.Add(f.Id)))
                {
                    var members = 0;
                    try { members = (await _fleets.ListMembersAsync(server, fleet.Id, session.CharacterId)).Count; }
                    catch { /* member count is best-effort */ }

                    var doctrine = "";
                    if (fleet.FleetCompositionId is { } compositionId)
                        try
                        {
                            var composition = await new ServerFleetCompositionClient(_fleets, server, session.CharacterId).GetAsync(compositionId);
                            doctrine = composition?.Composition.Name ?? "";
                        }
                        catch { /* doctrine name is best-effort */ }

                    var isMine = fleet.CreatorCharacterId == session.CharacterId;
                    built.Add(new DashboardFleetViewModel(fleet.Name, fleet.Activation, members, serverName, isMine, doctrine));
                }
            }
        }

        foreach (var fleet in built.OrderByDescending(f => f.Activation))
            Fleets.Add(fleet);
        ActiveFleetCount = built.Count(f => f.Activation == FleetActivation.Active);
        FormingFleetCount = built.Count(f => f.Activation == FleetActivation.Forming);
        OnPropertyChanged(nameof(HasFleets));
    }

    private async Task LoadFitsAsync()
    {
        LatestFits.Clear();
        if (_fitShare is null || _sessions is null)
        {
            SharedFitCount = 0;
            OnPropertyChanged(nameof(HasFits));
            return;
        }

        var built = new List<DashboardFitViewModel>();
        foreach (var server in await _sessions.ListServersAsync())
        {
            var serverName = _serverRegistry is null ? server : await _serverRegistry.DisplayNameAsync(server);
            (bool Ok, string Message, IReadOnlyList<SharedFitInfo> Fits) result;
            try { result = await _fitShare.GetSharedFitsAsync(server); }
            catch { continue; }
            if (!result.Ok)
                continue;
            built.AddRange(result.Fits.Select(f =>
                new DashboardFitViewModel(f.Name, f.SharedByCharacterName, f.ShipTypeId, _sdeNames?.TypeName(f.ShipTypeId) ?? "", serverName, f.SharedAt)));
        }

        // The server returns fits newest-first; surface a handful on the card + load their hull icons best-effort.
        foreach (var fit in built.Take(6))
        {
            LatestFits.Add(fit);
            if (_typeImages is not null)
                _ = fit.LoadHullIconAsync(_typeImages);
        }
        // "Shared today" = a real calendar-day count now that the server stamps SharedAt (decision 2026-06-17).
        var today = DateTimeOffset.Now.Date;
        SharedFitCount = built.Count(f => f.SharedAt != default && f.SharedAt.ToLocalTime().Date == today);
        OnPropertyChanged(nameof(HasFits));
    }

    /// <summary>Compact ISK formatting for the dashboard summary chip ("894.4k", "1.2M", "3.4B").</summary>
    private static string CompactIsk(long isk) => isk switch
    {
        >= 1_000_000_000 => (isk / 1_000_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (isk / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (isk / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => isk.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>One of your fleets on the dashboard — name, its in-game phase (Forming/Active) and a member/server line
/// that flags the ones you own.</summary>
public sealed class DashboardFleetViewModel(string name, FleetActivation activation, int memberCount, string serverName, bool isMine, string doctrine = "")
{
    public string Name => name;
    public FleetActivation Activation => activation;
    public bool IsForming => activation == FleetActivation.Forming;
    public bool IsMine => isMine;
    public string StateLabel => activation switch
    {
        FleetActivation.Active => "Active",
        FleetActivation.Concluded => "Concluded",
        _ => "Forming"
    };

    // "5 members · Homefront Vanguard · Aura · you own it" — doctrine inserted only when the fleet has one coupled.
    public string MetaLabel
    {
        get
        {
            var parts = new List<string> { $"{memberCount} member{(memberCount == 1 ? "" : "s")}" };
            if (!string.IsNullOrEmpty(doctrine)) parts.Add(doctrine);
            parts.Add(serverName);
            if (isMine) parts.Add("you own it");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One shared fit on the dashboard — hull + fit name, who shared it and from which server, with the hull icon.</summary>
public sealed partial class DashboardFitViewModel : ObservableObject
{
    public DashboardFitViewModel(string name, string sharedBy, int shipTypeId, string hullName, string serverName, DateTimeOffset sharedAt = default)
    {
        Name = name;
        SharedBy = sharedBy;
        ShipTypeId = shipTypeId;
        HullName = hullName;
        ServerName = serverName;
        SharedAt = sharedAt;
    }

    public string Name { get; }
    public string SharedBy { get; }
    public int ShipTypeId { get; }
    public string HullName { get; }
    public string ServerName { get; }
    public DateTimeOffset SharedAt { get; }

    /// <summary>"Muninn — Kite" when the hull resolves from the SDE, otherwise just the fit name.</summary>
    public string Display => string.IsNullOrEmpty(HullName) ? Name : $"{HullName} — {Name}";

    /// <summary>"RaymondKrah · Aura · 2h ago" — who shared it, from which server, and how long ago.</summary>
    public string SharedByLabel
    {
        get
        {
            var ago = RelativeTime(SharedAt);
            return string.IsNullOrEmpty(ago) ? $"{SharedBy} · {ServerName}" : $"{SharedBy} · {ServerName} · {ago}";
        }
    }

    /// <summary>Compact "x ago" for a shared timestamp ("just now", "5m ago", "2h ago", "yesterday", "3d ago").</summary>
    private static string RelativeTime(DateTimeOffset when)
    {
        if (when == default) return "";
        var delta = DateTimeOffset.Now - when.ToLocalTime();
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 2) return "yesterday";
        return $"{(int)delta.TotalDays}d ago";
    }

    [ObservableProperty] private Bitmap? _hullIcon;
    public bool HasHullIcon => HullIcon is not null;
    partial void OnHullIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasHullIcon));

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    public async Task LoadHullIconAsync(ITypeImageProvider images)
    {
        if (ShipTypeId <= 0)
            return;
        HullIcon = await images.GetImageAsync(ShipTypeId, TypeImageKind.Render, 64);
    }
}
