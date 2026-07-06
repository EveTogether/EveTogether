using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Transport;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Drives the Fleets window: the character's own fleets (create/edit/disband/invite), the public fleets to
/// discover + join, and the active-fleet participation (enter/leave → <see cref="IActiveFleetState"/>, which the
/// publisher reads). Fleets live per server; v1 targets the first coupled server of the most-recent session
/// (multi-server picking is a later refinement). The live member graphs are wired in increment 4.
/// </summary>
public sealed partial class FleetsViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IFleetTransportClient _fleets;
    private readonly IActiveFleetState _activeFleet;
    private readonly IFleetParticipation _participation; // Membership-driven publish set (what the publisher shares)
    private readonly IDialogService _dialogs;
    private readonly IClientSessionStore _sessions;
    private readonly IServerRegistry _serverRegistry;
    private readonly ClientFleetService _localFleets; // client-only fleets (no server)
    private readonly IFleetRepository _fleetRepository; // client-bound repo: reads local fleets/roster
    private readonly ICharacterRegistry _characters;
    private readonly IToastService _toasts;
    private readonly IFleetMetricsLauncher _metricsLauncher;
    private readonly IDisposable _fleetChangedSubscription;
    private readonly IRemoteBusConnector _busConnector;
    private readonly ICharacterInfoService? _characterInfo; // resolves owner names for fleets I don't own (best-effort)

    public FleetsViewModel(IServiceProvider services)
    {
        _services = services;
        _fleets = services.GetRequiredService<IFleetTransportClient>();
        _activeFleet = services.GetRequiredService<IActiveFleetState>();
        _participation = services.GetRequiredService<IFleetParticipation>();
        _dialogs = services.GetRequiredService<IDialogService>();
        _sessions = services.GetRequiredService<IClientSessionStore>();
        _serverRegistry = services.GetRequiredService<IServerRegistry>();
        _localFleets = services.GetRequiredService<ClientFleetService>();
        _fleetRepository = services.GetRequiredService<IFleetRepository>();
        _characters = services.GetRequiredService<ICharacterRegistry>();
        _toasts = services.GetRequiredService<IToastService>();
        _metricsLauncher = services.GetRequiredService<IFleetMetricsLauncher>();
        _characterInfo = services.GetRequiredService<ICharacterInfoService>();

        // A fleet's lifecycle/roster changed on a server (start/conclude/join/leave is pushed as fleet.changed):
        // reload so the lists AND the membership-driven participation set refresh live — that is what makes a member
        // start publishing metrics to a just-started fleet, and the roster reflect a join, without a reconnect/restart.
        _fleetChangedSubscription = services.GetRequiredService<IEventBus>().Subscribe<FleetChangedEvent>(_OnFleetChanged);

        // A server's bus connection can still be establishing when this window opens, so the construction-time load
        // below can come back empty. Reload when a server reaches Connected so already-existing fleets appear without
        // a client restart (previously only a fleet.changed event or a restart re-fetched the list).
        _busConnector = services.GetRequiredService<IRemoteBusConnector>();
        _busConnector.StateChanged += _OnServerConnectionStateChanged;

        _ = InitializeAsync();
    }

    private void _OnFleetChanged(FleetChangedEvent integrationEvent) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReloadAsync());

    private void _OnServerConnectionStateChanged(string serverAddress, ServerConnectionState state)
    {
        if (state == ServerConnectionState.Connected)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    // Disposed by FleetsWindow on Closed: without this the event-bus subscription and the connection-state handler
    // (and the whole VM graph they capture) outlive the window, leaking and adding a duplicate ReloadAsync per event.
    public void Dispose()
    {
        _fleetChangedSubscription.Dispose();
        _busConnector.StateChanged -= _OnServerConnectionStateChanged;
    }

    /// <summary>All fleets per coupled server in one list: owned, joined and discoverable fleets are merged
    /// into a single row each, grouped under a per-server header. One unified overview instead of the old
    /// Browser/Participating/My Fleets tabs — each row carries its own relationship + actions.</summary>
    public ObservableCollection<FleetServerGroupViewModel> ServerGroups { get; } = [];

    /// <summary>Client-only fleets: live purely in the local SQLite, no server. Manage + ENTER locally.</summary>
    public ObservableCollection<FleetViewModel> LocalFleets { get; } = [];

    /// <summary>Coupled servers that could not be reached this sweep — typically stale couplings (e.g. an old
    /// dev/test server left in the local DB). Surfaced with a DECOUPLE button so they can be removed; otherwise a
    /// fleet-less unreachable server has no row to act on.</summary>
    public ObservableCollection<FleetServerGroupViewModel> UnreachableServers { get; } = [];

    [ObservableProperty] private string _serverLabel = "Resolving server…";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _activeFleetLabel = "Not participating in a fleet.";
    [ObservableProperty] private bool _canInteract;
    [ObservableProperty] private bool _isParticipating;

    /// <summary>Client-only fleets need no coupled server — they are usable as soon as a local
    /// character exists, independent of <see cref="CanInteract"/> (the server gate).</summary>
    [ObservableProperty] private bool _canInteractLocal;

    /// <summary>Drives the visibility of the "unreachable servers" strip (only shown when there is something to act on).</summary>
    [ObservableProperty] private bool _hasUnreachableServers;

    /// <summary>Drives the "Local fleets" section header (hidden when there are no client-only fleets).</summary>
    [ObservableProperty] private bool _hasLocalFleets;

    private async Task InitializeAsync()
    {
        // Client-only fleets load first and independently of any server — the local tab works offline.
        await LoadLocalFleetsAsync();

        // a character can be coupled to several servers; the window aggregates them all rather than pinning the
        // first. The header reflects the server count; per-server detail lives in the grouped lists.
        var servers = await _sessions.ListServersAsync();
        CanInteract = servers.Count > 0;
        ServerLabel = servers.Count switch
        {
            0 => "Couple a character to a server first.",
            1 => await _serverRegistry.DisplayNameAsync(servers[0]),
            _ => $"{servers.Count} servers",
        };

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var active = _activeFleet.ActiveFleetId;

        // multi-server aggregation: a character can be coupled to several servers, so list EVERY coupled server
        // (not just the first) and group each list per server. Within a server, the list aggregates every coupled
        // character's fleets and tags each row with the character it belongs to.
        //
        // Each server is loaded INDEPENDENTLY and CONCURRENTLY: one server being down or slow must not block or blank
        // the others. Loading them sequentially meant an unreachable server stalled the whole sweep on its connect
        // timeout — the fleets of a reachable server then only appeared "after a while", or not at all. A server that
        // fails this sweep keeps its last-known fleets (rather than vanishing) and is reported in the status line.
        var servers = await _sessions.ListServersAsync();
        var loads = await Task.WhenAll(servers.Select(server => LoadServerAsync(server, active)));

        RebuildPerServer(ServerGroups, loads, load => load.Group);

        // Unreachable servers get their own strip with a DECOUPLE button — a fleet-less stale server (an old dev/test
        // coupling) otherwise has no row to remove it from.
        UnreachableServers.Clear();
        foreach (var load in loads.Where(l => !l.Ok))
            UnreachableServers.Add(new FleetServerGroupViewModel(load.ServerName, load.Server));
        HasUnreachableServers = UnreachableServers.Count > 0;

        if (UnreachableServers.Count > 0)
            StatusMessage = $"Could not reach {string.Join(", ", UnreachableServers.Select(g => g.ServerName))} — decouple below if stale.";

        UpdateParticipation();
    }

    /// <summary>Loads one coupled server's fleets in isolation. A transport failure (server down/unreachable) is caught
    /// and reported via <see cref="ServerLoad.Ok"/> = false instead of propagating, so it can't abort the other
    /// servers' loads. A disbanded fleet is Archived (soft-delete) so only Active ones show.</summary>
    private async Task<ServerLoad> LoadServerAsync(string server, long? active)
    {
        var sessions = await _sessions.LoadAllAsync(server);
        var serverName = sessions.Count == 0 ? server : await _serverRegistry.DisplayNameAsync(server);
        var group = new FleetServerGroupViewModel(serverName, server);

        if (sessions.Count == 0)
            return new ServerLoad(server, serverName, true, group);

        var coupledIds = sessions.Select(s => s.CharacterId).ToHashSet();

        try
        {
            // aggregate: a fleet may hold several of my coupled characters. List each fleet ONCE as a node and
            // show my characters in it as member leaves (stream B / B-2), instead of a flat row per character. Rows
            // are keyed by fleet id so the discoverable pass below can merge into the same row (one unified list).
            var rowsByFleet = new Dictionary<long, FleetViewModel>();
            var byFleet = new Dictionary<long, (FleetInfo Fleet, List<(int Id, string Name)> Chars)>();
            foreach (var session in sessions)
                foreach (var fleet in (await _fleets.ListMyFleetsAsync(server, session.CharacterId)).Where(f => f.State == FleetState.Active))
                {
                    if (!byFleet.TryGetValue(fleet.Id, out var entry))
                        byFleet[fleet.Id] = entry = (fleet, []);
                    entry.Chars.Add((session.CharacterId, session.CharacterName));
                }

            foreach (var (fleet, chars) in byFleet.Values)
            {
                // The node acts as the owner when I own the fleet (owner-gated actions authenticate as the owner),
                // otherwise as my first character in it.
                var ownerChar = chars.FirstOrDefault(c => c.Id == fleet.CreatorCharacterId);
                var acting = ownerChar.Id != 0 ? ownerChar : chars[0];
                var row = new FleetViewModel(fleet, acting.Id, acting.Name, server, serverName) { IsActive = active == fleet.Id, IsParticipating = true };
                await PopulateMembersAsync(row, fleet, chars, server, coupledIds);
                ResolveOwnerName(row);
                rowsByFleet[fleet.Id] = row;
                group.Fleets.Add(row);
            }

            // The discoverable browser is server-wide (not per character) — list it once as the server's most-recent
            // session; the per-row Join/Request picker chooses the actual character at action time. A fleet I'm
            // already in merges into its existing row (so one row shows joined + still-joinable state); a fleet I'm not
            // in becomes a new discoverable row.
            var recent = await _sessions.LoadAsync(server);
            var browserActingChar = recent?.CharacterId ?? 0;
            foreach (var fleet in await _fleets.ListOpenFleetsAsync(server))
            {
                var members = await _fleets.ListMembersAsync(server, fleet.Id);
                var memberIds = members.Select(m => m.CharacterId).ToHashSet();

                if (!rowsByFleet.TryGetValue(fleet.Id, out var row))
                {
                    row = new FleetViewModel(fleet, browserActingChar, recent?.CharacterName ?? "", server, serverName) { IsActive = active == fleet.Id };
                    ResolveOwnerName(row);
                    rowsByFleet[fleet.Id] = row;
                    group.Fleets.Add(row);
                }

                // Browser status: visibility + Forming/Active + a live member count + join eligibility
                // (a coupled character that isn't a member yet — joins/requests with another toon stay possible).
                row.MemberCount = members.Count;
                row.StatusLabel = $"{row.VisibilityLabel} · {row.ActivationLabel}";
                row.IsDiscoverable = true; // came from the open list → JOIN/REQUEST shows (disabled if no free character)
                // A free coupled character can still join even on a fleet I own/am in.
                row.CanJoinHere = coupledIds.Any(id => !memberIds.Contains(id));

                // B-1: the coupled doctrine + its per-role fill, so a pilot sees what a fleet flies and how full each
                // role is before joining. Only built once (skip if a member pass already added the doctrine + fill).
                if (fleet.FleetCompositionId is { } compositionId && row.RoleFill.Count == 0)
                {
                    var composition = await CompositionClientFor(server, browserActingChar).GetAsync(compositionId);
                    row.CompositionName = composition?.Composition.Name;
                    foreach (var fill in CompositionFillBuilder.Build(composition, members))
                        row.RoleFill.Add(fill);
                }
            }
        }
        catch (FleetTransportException)
        {
            return new ServerLoad(server, serverName, false, null);
        }

        return new ServerLoad(server, serverName, true, group);
    }

    /// <summary>Resolves the creator's name for a fleet I don't own (best-effort, off the UI thread via the metered
    /// ESI pipeline) so the row can show "Owner: &lt;name&gt;". My own fleets show "you" without a lookup.</summary>
    private void ResolveOwnerName(FleetViewModel row)
    {
        if (row.IsMine || _characterInfo is null)
            return;

        _ = Task.Run(async () =>
        {
            var name = await _characterInfo.GetNameAsync(row.Info.CreatorCharacterId);
            if (!string.IsNullOrWhiteSpace(name))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => row.OwnerName = name);
        });
    }

    /// <summary>Rebuilds one of the bound lists from the per-server loads, preserving order. A server that responded
    /// contributes its freshly-built group (if non-empty); a server that failed keeps the group it had before this
    /// sweep, so an unreachable server's fleets don't disappear on a transient blip.</summary>
    private static void RebuildPerServer(
        ObservableCollection<FleetServerGroupViewModel> target,
        IReadOnlyList<ServerLoad> loads,
        Func<ServerLoad, FleetServerGroupViewModel?> select)
    {
        var previous = new Dictionary<string, FleetServerGroupViewModel>();
        foreach (var group in target)
            if (group.ServerAddress is { } address)
                previous[address] = group;
        target.Clear();
        foreach (var load in loads)
        {
            var group = load.Ok
                ? select(load)
                : previous.GetValueOrDefault(load.Server);
            if (group is { Fleets.Count: > 0 })
                target.Add(group);
        }
    }

    /// <summary>One coupled server's fleet groups for a single reload, or a failure marker (<see cref="Ok"/> = false)
    /// when that server was unreachable — so a dead server is isolated from the rest of the multi-server sweep.</summary>
    private sealed record ServerLoad(
        string Server, string ServerName, bool Ok, FleetServerGroupViewModel? Group);

    // --- Member leaves (stream B / B-2): my characters in a fleet, with their fit + skill + a self-assign action. ---

    /// <summary>Builds the member leaf rows for a fleet node: each of my characters in the fleet with their role, the
    /// fit they fly, a can-fly badge and a SELECT FIT action scoped to the coupled doctrine. The fit
    /// picker assigns the pilot's OWN fit (master-plan §5; the server authorizes owner-or-self).</summary>
    private async Task PopulateMembersAsync(FleetViewModel row, FleetInfo fleet, IReadOnlyList<(int Id, string Name)> myChars, string? server, IReadOnlySet<int>? coupledIds = null)
    {
        var names = myChars.ToDictionary(c => c.Id, c => c.Name);
        var members = await ServerOrLocalClient(server, row.ActingCharacterId).ListMembersAsync(fleet.Id);

        // Join eligibility for the unified row: a coupled character that isn't a member yet can still join/request,
        // even when another of my characters is already in this fleet — and even when I own it.
        if (coupledIds is not null)
        {
            var memberIds = members.Select(m => m.CharacterId).ToHashSet();
            row.CanJoinHere = coupledIds.Any(id => !memberIds.Contains(id));
        }

        // The coupled doctrine scopes the picker and tags the assignment with the entry it fills.
        var composition = fleet.FleetCompositionId is { } compositionId
            ? await CompositionClientFor(server, row.ActingCharacterId).GetAsync(compositionId)
            : null;
        row.CompositionName = composition?.Composition.Name;

        var evaluator = _services.GetService<IMemberFitSkillEvaluator>();
        var portraits = _services.GetService<ICharacterPortraitProvider>();
        foreach (var member in members.Where(m => names.ContainsKey(m.CharacterId)))
        {
            var badge = evaluator is null ? null : await evaluator.EvaluateAsync(member.CharacterId, member.AssignedFit);
            await ReportOwnVerdictAsync(member, badge, server);
            // Skills this client doesn't know locally (no read_skills scope / not imported) still get a badge from the
            // pilot's OWN client's reported verdict, so a can-fly/skills-missing badge shows for every member, not only mine.
            badge ??= WireSkillBadge(member.FitSkillVerdict);
            var assignedFit = member.AssignedFit;
            // A non-owner character on a server fleet gets a per-leaf LEAVE (multi-box): pull this alt out while the
            // owner — and any other of my characters in the fleet — stays. The owner's own character never leaves.
            var canLeave = server is not null && member.CharacterId != fleet.CreatorCharacterId;
            var leaf = new FleetMemberRowViewModel(
                member.Id, member.CharacterId, names[member.CharacterId], RoleLabel(member.Role),
                assignedFit, badge,
                new AsyncRelayCommand(() => SelectMemberFitAsync(member, composition, server)),
                assignedFit is null ? null : new AsyncRelayCommand(() => FitDetailLauncher.OpenAsync(_services, _dialogs, assignedFit)),
                canLeave ? new AsyncRelayCommand(() => LeaveMemberAsync(server!, fleet.Id, member.CharacterId, names[member.CharacterId], fleet.Name)) : null,
                canLeave);
            row.Members.Add(leaf);
            if (portraits is not null)
                _ = leaf.LoadPortraitAsync(portraits);   // B-3 hex portrait, best-effort (opt-in images)
        }
    }

    /// <summary>cross-client: this client is the skill authority for its own characters, so it pushes
    /// the locally computed verdict to the backing store whenever it differs from what is stored — viewers who do
    /// not know the pilot's skills then show the can-fly/skills-missing badge from the wire verdict. A null badge (skills not known
    /// locally / no validator) is never reported: unknown must not overwrite a real verdict. Idempotence on the
    /// stored value keeps the report → fleet.changed → reload cycle from looping.</summary>
    private async Task ReportOwnVerdictAsync(FleetMemberInfo member, MemberSkillBadge? badge, string? server)
    {
        if (badge is null || member.AssignedFit is null)
            return;

        var verdict = badge.CanFly ? FitSkillVerdict.CanFly : FitSkillVerdict.MissingSkills;
        if (member.FitSkillVerdict == verdict)
            return;

        try
        {
            // Acting as the member's own character — the report is self-only on the server.
            await ServerOrLocalClient(server, member.CharacterId).ReportMemberFitVerdictAsync(member.Id, verdict);
        }
        catch (FleetTransportException)
        {
            // Best-effort: an unreachable server just means the verdict travels on a later reload.
        }
    }

    /// <summary>Display badge from a member's wire skill verdict (their own client's report) — the fallback
    /// when this client cannot compute the verdict itself. Unknown stays badge-less (unknown ≠ "can't fly").</summary>
    private static MemberSkillBadge? WireSkillBadge(FitSkillVerdict verdict) => verdict switch
    {
        FitSkillVerdict.CanFly => new MemberSkillBadge(CanFly: true, "Can fly this fit (reported by the pilot)"),
        FitSkillVerdict.MissingSkills => new MemberSkillBadge(CanFly: false, "Missing skills (reported by the pilot)"),
        _ => null
    };

    /// <summary>The (server, character)-bound fleet client for member reads/assigns — gRPC for a server fleet, the
    /// local CQRS seam for a client-only one. Mirrors how Manage/OpenMetrics resolve their client.</summary>
    private IFleetClient ServerOrLocalClient(string? server, int actingCharacterId) =>
        server is null
            ? new LocalFleetClient(_localFleets, _fleetRepository, _characters, actingCharacterId)
            : new ServerFleetClient(_fleets, server, actingCharacterId);

    private IFleetCompositionClient CompositionClientFor(string? server, int actingCharacterId) =>
        server is null
            ? new LocalFleetCompositionClient(_localFleets,
                _services.GetRequiredService<EveUtils.Shared.Modules.Fleet.Composition.Repositories.IFleetCompositionRepository>(),
                actingCharacterId)
            : new ServerFleetCompositionClient(_fleets, server, actingCharacterId);

    private static string RoleLabel(FleetRole role) => role switch
    {
        FleetRole.FleetCommander => "Fleet Commander",
        FleetRole.WingCommander => "Wing Commander",
        FleetRole.SquadCommander => "Squad Commander",
        FleetRole.SquadMember => "Squad Member",
        _ => "Unassigned"
    };

    /// <summary>SELECT FIT on a member leaf (stream B / B-2): the pilot picks their OWN fit from the composition-scoped
    /// single picker; the assignment acts as the member's character, which the server authorizes as owner-or-self
    /// (master-plan §5). Tags the entry the fit fills when it comes from the doctrine, then reloads.</summary>
    private async Task SelectMemberFitAsync(FleetMemberInfo member, FleetCompositionDetail? composition, string? server)
    {
        var picker = new FitPickerViewModel(_services, FitPickerMode.Single, alreadyAdded: null,
            composition: composition, currentFitHash: member.AssignedFit?.ContentHash,
            skillCheckCharacterId: member.CharacterId);

        var fit = await _dialogs.PickFitAsync(picker);
        if (fit is null)
            return;

        var entryId = composition?.Roles.SelectMany(r => r.Entries)
            .FirstOrDefault(e => string.Equals(e.Fit.ContentHash, fit.ContentHash, StringComparison.OrdinalIgnoreCase))?.Id;

        var assigned = await ServerOrLocalClient(server, member.CharacterId).AssignMemberFitAsync(member.Id, fit, entryId);
        StatusMessage = assigned.Ok ? $"Assigned {fit.FitName}." : $"Assign failed: {assigned.Message}";
        if (assigned.Ok)
            await ReloadAsync();
    }

    // The publisher shares metrics for the fleets the client is in — but only once a server fleet is
    // ACTIVE (the FC pressed Start). Signing up in advance to a Forming fleet (e.g. created Tuesday for Saturday) is
    // membership without broadcast: you only share when it actually starts. Browser rows are excluded (discoverable ≠
    // joined); client-only fleets are local-only so they always feed your own graphs. Refreshed on every (re)load so a
    // join/leave/kick/start is reflected on the next sweep.
    private void UpdateParticipation()
    {
        var participants = new List<FleetParticipant>();
        foreach (var row in ServerGroups.SelectMany(g => g.Fleets).Where(r => r.IsParticipating))
            if (row.Info.Activation == FleetActivation.Active)
            {
                // Per-fleet aggregation (B-2): publish metrics for EVERY one of my characters in the fleet, not only
                // the acting one — multi-boxing several characters into one fleet must feed all their graphs.
                if (row.Members.Count > 0)
                    foreach (var leaf in row.Members)
                        participants.Add(new FleetParticipant(leaf.CharacterId, row.Id, ClientOnly: false));
                else
                    participants.Add(new FleetParticipant(row.ActingCharacterId, row.Id, ClientOnly: false));
            }
        foreach (var row in LocalFleets)
            // Same per-fleet aggregation as server fleets: a local fleet multi-boxing several of my characters must
            // feed every member's graph, not only the acting one — otherwise the metrics window shows data for the
            // acting character alone and the rest flatline.
            if (row.Members.Count > 0)
                foreach (var leaf in row.Members)
                    participants.Add(new FleetParticipant(leaf.CharacterId, row.Id, ClientOnly: true));
            else
                participants.Add(new FleetParticipant(row.ActingCharacterId, row.Id, ClientOnly: true));
        _participation.Set(participants);
    }

    [RelayCommand]
    private Task Refresh() => ReloadAsync();

    /// <summary>Decouples every one of my characters from this server and removes it from the list — for a
    /// stale/unreachable server lingering in the multi-server view. The per-character settings dialog still offers
    /// single-character decouple.</summary>
    [RelayCommand]
    private async Task DecoupleServer(FleetServerGroupViewModel? group)
    {
        if (group?.ServerAddress is not { } server)
            return;

        if (!await _dialogs.ConfirmAsync(
                "Decouple server",
                $"Decouple all your characters from {group.ServerName}? The server sessions are revoked and it is removed from the list.",
                okText: "Decouple"))
            return;

        await _services.GetRequiredService<ServerCouplingService>().DecoupleServerAsync(server);
        StatusMessage = $"Decoupled from {group.ServerName}.";
        await InitializeAsync(); // the coupled-server set changed → recompute the header/CanInteract + reload the lists.
    }

    // --- Client-only fleets: all local, via ClientFleetService over the client DbContext. No server. ---

    /// <summary>Loads the client-only fleets created by any local character into <see cref="LocalFleets"/>.</summary>
    private async Task LoadLocalFleetsAsync()
    {
        LocalFleets.Clear();
        var active = _activeFleet.ActiveFleetId;
        var characters = await _characters.GetAllAsync();
        CanInteractLocal = characters.Count > 0;

        // Every local character is a potential member-leaf name (a client-only fleet can hold any of them).
        var localChars = characters
            .Where(c => c.EsiCharacterId is not null)
            .Select(c => (Id: c.EsiCharacterId!.Value, c.Name))
            .ToList();

        foreach (var character in characters)
        {
            var ownerId = character.EsiCharacterId ?? 0;
            if (ownerId == 0)
                continue;

            foreach (var fleet in await _fleetRepository.ListByCreatorAsync(ownerId))
            {
                if (!fleet.IsClientOnly || fleet.State != FleetState.Active || fleet.Activation == FleetActivation.Concluded)
                    continue; // only client-only, still-active, not-concluded fleets show on this tab (concluded is hidden everywhere).

                var info = ToInfo(fleet);
                var row = new FleetViewModel(info, ownerId, character.Name) { IsActive = active == fleet.Id };
                row.StatusLabel = "Local · client-only";
                row.IsParticipating = true; // a client-only fleet is always "yours" — you feed its metrics locally.
                // Same member-leaf treatment as server fleets, but over the local CQRS seam (server: null) — shows
                // which local characters are in it, their fit, can-fly badge and a self-assign/open-fit action.
                await PopulateMembersAsync(row, info, localChars, server: null);
                LocalFleets.Add(row);
            }
        }

        HasLocalFleets = LocalFleets.Count > 0;
        UpdateParticipation();
    }

    private static FleetInfo ToInfo(FleetEntity fleet) => new(
        fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
        fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation, fleet.FleetCompositionId,
        fleet.EsiFleetId, fleet.EsiFleetBossId);

    /// <summary>Creates a client-only fleet: pick the owning local toon, name it, persist locally.</summary>
    [RelayCommand]
    private async Task NewLocalFleet()
    {
        if (!CanInteractLocal)
            return;

        var characters = await _characters.GetAllAsync();
        var options = characters
            .Where(c => c.EsiCharacterId is not null)
            .Select(c => new CharacterPickOption(c.EsiCharacterId!.Value, c.Name, "local character", Enabled: true))
            .ToList();
        if (options.Count == 0)
        {
            StatusMessage = "Add a local character first.";
            return;
        }

        var ownerId = await _dialogs.PickCharacterAsync("Owner of the local fleet", options);
        if (ownerId is null)
            return;

        var name = await _dialogs.PromptTextAsync("New local fleet", "Fleet name");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var created = await _localFleets.CreateLocalFleetAsync(name, null, ownerId.Value);
        StatusMessage = created.IsSuccess
            ? $"Created local fleet '{name}'."
            : $"Create failed: {created.Messages.FirstOrDefault()?.Text}";
        if (created.IsSuccess)
            await LoadLocalFleetsAsync();
    }

    /// <summary>Adds one of the owner's own local characters to a client-only fleet (non-external member).</summary>
    [RelayCommand]
    private async Task AddLocalCharacter(FleetViewModel? row)
    {
        if (row is null)
            return;

        var characters = await _characters.GetAllAsync();
        var options = characters
            .Where(c => c.EsiCharacterId is not null)
            .Select(c => new CharacterPickOption(c.EsiCharacterId!.Value, c.Name, "local character", Enabled: true))
            .ToList();
        var characterIds = await _dialogs.PickCharactersAsync("Add local character(s) to fleet", options);
        if (characterIds is null || characterIds.Count == 0)
            return;

        var added = 0;
        string? lastError = null;
        foreach (var characterId in characterIds)
        {
            var result = await _localFleets.AddLocalCharacterAsync(row.Id, characterId, row.Info.CreatorCharacterId);
            if (result.IsSuccess) added++;
            else lastError = result.Messages.FirstOrDefault()?.Text;
        }

        StatusMessage = lastError is null
            ? $"Added {added} character{(added == 1 ? "" : "s")}."
            : $"Added {added}; failed: {lastError}";
    }

    /// <summary>Adds an external EVE pilot (no local session) to a client-only fleet on trust.</summary>
    [RelayCommand]
    private async Task AddLocalExternal(FleetViewModel? row)
    {
        if (row is null)
            return;

        var lookup = _services.GetRequiredService<IExternalCharacterLookup>();
        var characterId = await _dialogs.AddExternalMemberAsync(lookup);
        if (characterId is null)
            return;

        var added = await _localFleets.AddExternalAsync(row.Id, characterId.Value, row.Info.CreatorCharacterId);
        StatusMessage = added.IsSuccess ? "Added external pilot." : $"Failed: {added.Messages.FirstOrDefault()?.Text}";
    }

    /// <summary>Opens the live metrics for a client-only fleet (formerly "Enter local"): selects it for the inline
    /// panel and opens the metrics window. Its metrics already stay local (client-only routing).</summary>
    [RelayCommand]
    private async Task OpenMetricsLocal(FleetViewModel? row)
    {
        if (row is null)
            return;

        _activeFleet.Enter(row.Id, row.Info.CreatorCharacterId, clientOnly: true); // selects for the inline panel + LEAVE
        SetActive(row.Id, row.Name);

        var client = new LocalFleetClient(_localFleets, _fleetRepository, _characters, row.Info.CreatorCharacterId);
        _dialogs.ShowFleetMetrics(new FleetMetricsViewModel(_services, client, row.Info));
        StatusMessage = $"Metrics for local fleet '{row.Name}'.";
    }

    /// <summary>Disbands a client-only fleet: archives it locally via the Shared DisbandFleet handler.</summary>
    [RelayCommand]
    private async Task DisbandLocal(FleetViewModel? row)
    {
        if (row is null)
            return;

        if (!await _dialogs.ConfirmAsync("Disband local fleet", $"Disband '{row.Name}'? It will be archived.", okText: "Disband"))
            return;

        // If we're locally participating in this fleet, stop first so the active state doesn't dangle.
        if (_activeFleet.ActiveFleetId == row.Id)
        {
            _activeFleet.Leave();
            SetActive(null, null);
        }

        var disbanded = await _localFleets.DisbandFleetAsync(row.Id, row.Info.CreatorCharacterId);
        StatusMessage = disbanded.IsSuccess
            ? $"Disbanded local fleet '{row.Name}'."
            : $"Disband failed: {disbanded.Messages.FirstOrDefault()?.Text}";
        if (disbanded.IsSuccess)
        {
            _toasts.Show($"Disbanded local fleet '{row.Name}'");
            await LoadLocalFleetsAsync();
        }
        else
        {
            _toasts.Show("Disband failed",
                disbanded.Messages.FirstOrDefault()?.Text ?? $"Could not disband '{row.Name}'.", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task NewFleet()
    {
        if (!CanInteract)
            return;

        // with several coupled servers, choose which one the fleet is created on.
        var server = await PickServerAsync("Create the fleet on which server?");
        if (server is null)
            return;

        var result = await _dialogs.EditFleetAsync(null);
        if (result is null)
            return;

        // with several characters coupled to this server, pick which one creates (and thus owns) the fleet.
        var ownerId = await PickActingCharacterAsync(server, "Create the fleet as which character?");
        if (ownerId is null)
            return;

        var created = await _fleets.CreateFleetAsync(
            server, result.Name, result.Description, result.Visibility,
            FleetOfflineBehavior.StayOffline, result.FromTime, result.ToTime, ownerId.Value);
        StatusMessage = created.Ok ? $"Created '{result.Name}'." : $"Create failed: {created.Message}";
        if (created.Ok)
            await ReloadAsync();
    }

    /// <summary>multi-character: pick which coupled character performs a per-character action (create/join).
    /// One coupling on this server → use it without asking; none → null so the caller aborts.</summary>
    private async Task<int?> PickActingCharacterAsync(string serverAddress, string prompt, IReadOnlySet<int>? exclude = null)
    {
        var sessions = (await _sessions.LoadAllAsync(serverAddress))
            .Where(s => exclude is null || !exclude.Contains(s.CharacterId))
            .ToList();
        if (sessions.Count == 0)
            return null;
        if (sessions.Count == 1)
            return sessions[0].CharacterId;

        var options = sessions
            .Select(s => new CharacterPickOption(s.CharacterId, s.CharacterName, "coupled", Enabled: true))
            .ToList();
        return await _dialogs.PickCharacterAsync(prompt, options);
    }

    /// <summary>Multi-select variant of <see cref="PickActingCharacterAsync"/> for bulk actions (join / add several
    /// toons at once). One eligible coupling → use it without asking; none → null; several → the multi-select picker.</summary>
    private async Task<IReadOnlyList<int>?> PickActingCharactersAsync(string serverAddress, string prompt, IReadOnlySet<int>? exclude = null)
    {
        var sessions = (await _sessions.LoadAllAsync(serverAddress))
            .Where(s => exclude is null || !exclude.Contains(s.CharacterId))
            .ToList();
        if (sessions.Count == 0)
            return null;
        if (sessions.Count == 1)
            return [sessions[0].CharacterId];

        var options = sessions
            .Select(s => new CharacterPickOption(s.CharacterId, s.CharacterName, "coupled", Enabled: true))
            .ToList();
        return await _dialogs.PickCharactersAsync(prompt, options);
    }

    /// <summary>pick which coupled server an action targets. One coupled server → use it without asking;
    /// none → null so the caller aborts; several → the server picker.</summary>
    private async Task<string?> PickServerAsync(string prompt)
    {
        var servers = await _sessions.ListServersAsync();
        if (servers.Count == 0)
            return null;
        if (servers.Count == 1)
            return servers[0];

        var options = new List<ServerPickOption>();
        foreach (var server in servers)
            options.Add(new ServerPickOption(server, await _serverRegistry.DisplayNameAsync(server)));
        return await _dialogs.SelectServerAsync(prompt, options);
    }

    [RelayCommand]
    private async Task EditFleet(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        var result = await _dialogs.EditFleetAsync(row.Info);
        if (result is null)
            return;

        var edited = await _fleets.EditFleetAsync(
            server, row.Id, result.Name, result.Description, result.Visibility,
            FleetOfflineBehavior.StayOffline, result.FromTime, result.ToTime, row.ActingCharacterId);
        StatusMessage = edited.Ok ? $"Saved '{result.Name}'." : $"Save failed: {edited.Message}";
        if (edited.Ok)
            await ReloadAsync();
    }

    [RelayCommand]
    private async Task Disband(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        if (!await _dialogs.ConfirmAsync("Disband fleet", $"Disband '{row.Name}'? It will be archived.", okText: "Disband"))
            return;

        var disbanded = await _fleets.DisbandFleetAsync(server, row.Id, row.ActingCharacterId);
        StatusMessage = disbanded.Ok ? $"Disbanded '{row.Name}'." : $"Disband failed: {disbanded.Message}";
        if (disbanded.Ok)
        {
            _toasts.Show($"Disbanded '{row.Name}'");
            await ReloadAsync();
        }
        else
        {
            _toasts.Show("Disband failed",
                string.IsNullOrWhiteSpace(disbanded.Message) ? $"Could not disband '{row.Name}'." : disbanded.Message,
                ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task Join(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        // pick which coupled character joins — excluding any of mine already in the fleet (can't join twice).
        var members = (await _fleets.ListMembersAsync(server, row.Id)).Select(m => m.CharacterId).ToHashSet();
        var coupled = await _sessions.LoadAllAsync(server);
        if (coupled.All(s => members.Contains(s.CharacterId)))
        {
            // No character connected to THIS server can join: either none is coupled here, or every coupled one is
            // already a member. Client characters that aren't coupled to this server are invisible to the join, so
            // spell out the coupling situation with a toast — a silent status-bar line read as "nothing happened"
            // and left the user wondering why their other characters can't join.
            var serverName = await _serverRegistry.DisplayNameAsync(server);
            var reason = coupled.Count == 0
                ? $"No character is connected to {serverName}. Couple one to this server to join '{row.Name}'."
                : $"Every character connected to {serverName} is already in '{row.Name}'. Couple another character to {serverName} to join with it.";
            StatusMessage = reason;
            _toasts.Show("Can't join", reason, ToastKind.Information);
            return;
        }

        // Multi-select: join with one or several of my coupled characters at once.
        var charIds = await PickActingCharactersAsync(server, $"Join '{row.Name}' as which character(s)?", members);
        if (charIds is null || charIds.Count == 0)
            return;

        var joinedNames = new List<string>();
        string? lastFailure = null;
        foreach (var charId in charIds)
        {
            var joined = await _fleets.JoinFleetAsync(server, row.Id, charId);
            if (joined.Ok)
                joinedNames.Add(coupled.FirstOrDefault(s => s.CharacterId == charId)?.CharacterName ?? $"char {charId}");
            else
                lastFailure = joined.Message;
        }

        // Always confirm with a toast — the single-character path otherwise enters silently (only the easily-missed
        // status bar changed).
        if (joinedNames.Count > 0)
        {
            var who = joinedNames.Count == 1 ? $"as {joinedNames[0]}" : string.Join(", ", joinedNames);
            StatusMessage = $"Joined '{row.Name}' ({string.Join(", ", joinedNames)}).";
            _toasts.Show($"Joined '{row.Name}'", who);
            await ReloadAsync();
        }
        if (lastFailure is not null)
        {
            StatusMessage = $"Join failed: {lastFailure}";
            _toasts.Show("Join failed",
                string.IsNullOrWhiteSpace(lastFailure) ? $"Could not join '{row.Name}'." : lastFailure, ToastKind.Error);
        }
    }

    /// <summary>Opens the per-fleet roster + lifecycle window for one of the acting character's fleets.</summary>
    [RelayCommand]
    private void Manage(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        // the roster acts as the row's character (the owner for my fleets), not whichever was coupled most
        // recently — so owner-gated roster actions authenticate as the actual owner. The server-bound IFleetClient
        // seam carries the (server, character) context so the roster window stays transport-agnostic.
        var client = new ServerFleetClient(_fleets, server, row.ActingCharacterId);
        var compositions = new ServerFleetCompositionClient(_fleets, server, row.ActingCharacterId);
        _dialogs.ShowRoster(new FleetRosterViewModel(
            _services, client, row.Info, isOwner: row.IsMine, row.ActingCharacterId,
            onActivationChanged: ReloadAsync, compositions: compositions));
    }

    /// <summary>Opens the SAME roster window for a client-only fleet via the local IFleetClient seam — full
    /// management (tree, move, add/delete wings+squads, externals, unassign) without a server or gRPC.</summary>
    [RelayCommand]
    private void ManageLocal(FleetViewModel? row)
    {
        if (row is null)
            return;

        var client = new LocalFleetClient(_localFleets, _fleetRepository, _characters, row.Info.CreatorCharacterId);
        var compositions = new LocalFleetCompositionClient(_localFleets,
            _services.GetRequiredService<EveUtils.Shared.Modules.Fleet.Composition.Repositories.IFleetCompositionRepository>(),
            row.Info.CreatorCharacterId);
        _dialogs.ShowRoster(new FleetRosterViewModel(
            _services, client, row.Info, isOwner: true, row.Info.CreatorCharacterId,
            onActivationChanged: ReloadAsync, compositions: compositions));
    }

    /// <summary>
    /// Per-fleet sharing override: choose what each of my characters in this fleet shares (use global default / share /
    /// don't share), with an "apply to all my characters" shortcut. Overrides the global Settings for this fleet only.
    /// </summary>
    [RelayCommand]
    private async Task OpenSharing(FleetViewModel? row)
    {
        if (row is null)
            return;

        // My characters in this fleet: the same fleet can appear once per coupled character.
        var myCharacters = ServerGroups.SelectMany(g => g.Fleets).Concat(LocalFleets)
            .Where(r => r.Id == row.Id)
            .Select(r => (Id: r.ActingCharacterId, Name: string.IsNullOrWhiteSpace(r.CharacterName) ? $"Char {r.ActingCharacterId}" : r.CharacterName))
            .DistinctBy(c => c.Id)
            .ToList();
        if (myCharacters.Count == 0)
            myCharacters.Add((row.ActingCharacterId, row.CharacterName));

        MetricShareSnapshot snapshot;
        using (var scope = _services.CreateScope())
        {
            var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
            snapshot = new MetricShareSnapshot(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
        }

        var vm = new FleetShareViewModel(row.Name, row.Id, myCharacters, snapshot);
        if (!await _dialogs.ShowFleetSharingAsync(vm))
            return;

        using (var scope = _services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            foreach (var (_, key, value) in vm.BuildOverrides())
                await dispatcher.Send(new SetSettingCommand(key, value));
        }

        StatusMessage = $"Sharing updated for '{row.Name}'.";
    }

    /// <summary>Requests to join an invite-only fleet from DISCOVER; the owner answers via their inbox.</summary>
    [RelayCommand]
    private async Task Request(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        // the discover browser is server-wide (its rows carry the most-recent session), so explicitly pick WHICH
        // coupled character requests rather than silently defaulting to the most-recent one. Exclude my characters
        // already in the fleet (can't request twice) and the fleet's owner (can't request to join a fleet you own);
        // the server enforces the same rules, this just keeps the picker honest.
        var blocked = (await _fleets.ListMembersAsync(server, row.Id)).Select(m => m.CharacterId).ToHashSet();
        blocked.Add(row.Info.CreatorCharacterId);
        var coupled = await _sessions.LoadAllAsync(server);
        if (coupled.All(s => blocked.Contains(s.CharacterId)))
        {
            StatusMessage = $"None of your characters can request to join '{row.Name}'.";
            return;
        }

        var charId = await PickActingCharacterAsync(server, $"Request to join '{row.Name}' as which character?", blocked);
        if (charId is null)
            return;

        var requested = await _fleets.RequestToJoinAsync(server, row.Id, charId.Value);
        StatusMessage = requested.Ok ? $"Join request sent for '{row.Name}'." : $"Request failed: {requested.Message}";
    }

    /// <summary>
    /// Opens the live metrics for a server fleet (formerly "Enter"). Participation is membership-driven, so there
    /// is no server "enter" call — being a connected member already shares this fleet's metrics. This selects the fleet
    /// for the in-window live panel and opens the metrics window (view-only).
    /// </summary>
    [RelayCommand]
    private async Task OpenMetrics(FleetViewModel? row)
    {
        if (row?.ServerAddress is not { } server)
            return;

        _activeFleet.Enter(row.Id, row.ActingCharacterId, server); // selects the fleet (+ its server) for the inline panel + LEAVE
        SetActive(row.Id, row.Name);

        await _metricsLauncher.LaunchAsync(server, row.Id, row.ActingCharacterId, row.Info);
        StatusMessage = $"Metrics for '{row.Name}'.";
    }

    [RelayCommand]
    private async Task Leave()
    {
        // A client-only fleet has no server session to leave — just clear the local active state.
        if (_activeFleet.IsActiveFleetClientOnly)
        {
            _activeFleet.Leave();
            SetActive(null, null);
            StatusMessage = "Left the local fleet.";
            return;
        }

        if (_activeFleet.ActiveServerAddress is not { } server || _activeFleet.ActiveFleetId is not { } fleetId)
            return;

        // several of my characters can be in this fleet — leave each, from THIS fleet specifically.
        var ok = true;
        string? lastError = null;
        foreach (var characterId in _activeFleet.ActiveCharacterIds.ToList())
        {
            var left = await _fleets.LeaveFleetAsync(server, fleetId, characterId);
            if (!left.Ok)
            {
                ok = false;
                lastError = left.Message;
            }
        }

        _activeFleet.Leave();
        SetActive(null, null);
        await ReloadAsync(); // membership changed → refresh the listing + the publish participation set
        StatusMessage = ok ? "Left the fleet." : $"Leave failed: {lastError}";
    }

    /// <summary>
    /// Leaves a fleet with one specific character: pulls a single member-leaf's character out of the
    /// fleet without touching my other characters in it. Bound to the per-leaf LEAVE shown for my non-owner characters.
    /// </summary>
    private async Task LeaveMemberAsync(string server, long fleetId, int characterId, string characterName, string fleetName)
    {
        var left = await _fleets.LeaveFleetAsync(server, fleetId, characterId);
        if (!left.Ok)
        {
            StatusMessage = $"Leave failed: {left.Message}";
            _toasts.Show("Leave failed",
                string.IsNullOrWhiteSpace(left.Message) ? $"{characterName} could not leave '{fleetName}'." : left.Message,
                ToastKind.Error);
            return;
        }

        // If the inline panel was showing this fleet, clear it.
        if (_activeFleet.ActiveFleetId == fleetId)
        {
            _activeFleet.Leave();
            SetActive(null, null);
        }

        await ReloadAsync();
        StatusMessage = $"{characterName} left '{fleetName}'.";
        _toasts.Show($"Left '{fleetName}'", characterName);
    }

    private void SetActive(long? fleetId, string? fleetName)
    {
        IsParticipating = fleetId is not null;
        ActiveFleetLabel = fleetName is null ? "Not participating in a fleet." : $"ACTIVE: {fleetName}";
        foreach (var row in ServerGroups.SelectMany(g => g.Fleets).Concat(LocalFleets))
            row.IsActive = fleetId is not null && row.Id == fleetId;
    }
}
