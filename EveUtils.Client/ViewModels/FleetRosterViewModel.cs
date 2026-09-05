using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;   // Result / ResultMessage, used by the ESI mirror helpers below
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Drives the per-fleet roster window: the left member list (accepted members + pending invites
/// + pending join-requests with badges) and the right EVE tree. Each level's commander is shown in its node header
/// (E1 — Fleet/Wing/Squad "(n) · FC/WC/SC: Name"); the remaining members hang underneath. All composition actions run
/// through the right-click context menu (E2): the EVE move-cascade, invite-to-position, assign-accepted-to-position,
/// remove, and transfer-ownership — built here as commands closing over the target member/position, so the node and
/// member view-models stay logic-free carriers. Pending invites/join-requests are answered through the invite flow /
/// inbox, the same path the rest of the fleet roster uses. Unplaced members (no wing/squad position) are NOT structure nodes —
/// they live in the left list only, which carries the same manage menu, until the owner places them.
/// </summary>
public sealed partial class FleetRosterViewModel : ObservableObject, IDisposable
{
    private readonly IFleetClient _fleets;
    private readonly IFleetCompositionClient? _compositions;
    private long? _coupledCompositionId;
    private FleetCompositionDetail? _coupledComposition;
    private long? _esiFleetId;       // coupled live in-game fleet; null = not coupled
    private int? _esiFleetBossId;
    private readonly IDialogService _dialogs;
    private readonly IExternalCharacterLookup _lookup;
    private readonly IMemberFitSkillEvaluator? _skillEvaluator;
    private readonly IServiceProvider _services;
    private readonly IToastService _toasts;
    private readonly ISdeNameResolver _shipNames;
    private readonly IFleetRosterWatch _rosterWatch;
    private readonly IDisposable _rosterSubscription;
    private readonly IDisposable? _metricSubscription;
    private readonly IDisposable? _presenceSubscription;
    private readonly ILocalCharacterPresence? _presence;
    private readonly DispatcherTimer _presenceSweep;

    // Every member node this window is showing, so the presence sweep can reach them without walking the tree. A list
    // per pilot and not one node: a placed member gets a node in the structure tree AND a second one behind their row
    // in the left list, and a sweep that reached only the last one built would leave the other surface reading the
    // verdict from whenever the roster last reloaded.
    private readonly Dictionary<int, List<MemberNodeViewModel>> _nodesByCharacter = [];

    // What each member's own client last said about their game. This window is not a metrics screen and shows no
    // figures, but it subscribes to the one stream that carries the answer: a pilot whose EVE is closed while their
    // EVE Together runs goes on publishing, so their LastSeenAt stays fresh and silence alone would call them
    // present. The reported half is the only thing that can tell the roster otherwise (ET-70).
    private readonly Dictionary<int, (PresenceState State, DateTimeOffset At)> _reportedPresence = [];

    // The announcements this window made for its own mutations, so it does not reload a second time for news it is
    // already showing. Matched on identity, never on value: two changes that describe the same pilot are still two
    // pieces of news, and only the one this window raised itself may be skipped.
    private readonly List<FleetRosterChange> _ownAnnouncements = [];

    // The can-fly verdict per member id, recomputed each reload from the member's assigned fit + cached skills.
    private IReadOnlyDictionary<long, MemberSkillBadge> _skillBadges = new Dictionary<long, MemberSkillBadge>();

    private readonly FleetInfo _fleet;
    private readonly bool _isOwner;
    private readonly int _actingCharacterId;
    private readonly Dictionary<int, string> _nameById = new();

    // Invoked after an action that changes the fleet's activation (Start/Conclude) so the parent Fleets list can
    // refresh its rows — this is a non-modal window, so the list cannot otherwise know the activation moved on.
    private readonly Func<Task>? _onActivationChanged;

    // The last loaded roster, kept so "assign accepted member to position" can offer the current members.
    private IReadOnlyList<FleetMemberInfo> _members = [];

    // My own characters that are members of this fleet and not its owner — the LEAVE candidates (multi-box: several of
    // my characters can be in one fleet, each leavable on its own). Recomputed every reload.
    private IReadOnlyList<(int Id, string Name)> _leavableCharacters = [];

    // The transport is bound to its fleet's context (server + acting character, or the local repository) by the
    // caller (IFleetClient seam) — this window serves both a server-backed and a client-only fleet unchanged.
    public FleetRosterViewModel(
        IServiceProvider services, IFleetClient fleets, FleetInfo fleet, bool isOwner, int actingCharacterId = 0,
        Func<Task>? onActivationChanged = null, IFleetCompositionClient? compositions = null)
    {
        _services = services;
        _fleets = fleets;
        _dialogs = services.GetRequiredService<IDialogService>();
        _toasts = services.GetRequiredService<IToastService>();
        _lookup = services.GetRequiredService<IExternalCharacterLookup>();
        _skillEvaluator = services.GetRequiredService<IMemberFitSkillEvaluator>();
        _shipNames = FitNameResolverFactory.For(services);

        _fleet = fleet;
        _isOwner = isOwner;
        _actingCharacterId = actingCharacterId;
        _onActivationChanged = onActivationChanged;
        _compositions = compositions;
        _coupledCompositionId = fleet.FleetCompositionId;
        _esiFleetId = fleet.EsiFleetId;
        _esiFleetBossId = fleet.EsiFleetBossId;
        // Seed the toggles from the stored state via the backing fields so the source-generated OnChanged hooks don't
        // fire a redundant save on load (they only persist a genuine user toggle).
        _esiAutoApplyStructure = fleet.EsiAutoApplyStructure;
        _esiAutoInviteMembers = fleet.EsiAutoInviteMembers;

        FleetName = fleet.Name;
        IsOwner = isOwner;
        UpdateActivationLabel(fleet.Activation);

        // Any change to this fleet's roster — someone else joining or leaving, the fleet starting, a pilot removed in
        // fleet metrics or on the browser card — arrives on the one shared watch, which folds in the server's
        // fleet.changed as well (ET-52). Reload so this window shows it without being reopened. Disposed on close.
        _rosterWatch = services.GetRequiredService<IFleetRosterWatch>();
        _rosterSubscription = _rosterWatch.Subscribe(_OnRosterChanged);

        // Who is actually here (ET-70). Two evidence paths, and this window needs both: our own pilots' EVE clients,
        // which the local sweep sees directly, and everyone else's, which only reaches us over the fleet's metric
        // stream — or, when their client is gone, by not reaching us at all.
        _presence = services.GetService<ILocalCharacterPresence>();
        _presenceSubscription = _presence?.Subscribe(() => _RefreshPresence(DateTimeOffset.UtcNow));
        _metricSubscription = services.GetService<IEventBus>()?.Subscribe<FleetMetricEvent>(_OnFleetMetric);
        _presenceSweep = new DispatcherTimer { Interval = FleetMetricsViewModel.PresenceSweepInterval };
        _presenceSweep.Tick += (_, _) => _RefreshPresence(DateTimeOffset.UtcNow);
        _presenceSweep.Start();

        _ = ReloadAsync();
    }

    private void _OnFleetMetric(FleetMetricEvent integrationEvent)
    {
        var sample = integrationEvent.Data;
        if (sample.FleetId != _fleet.Id || sample.Kind is not MetricKind.Presence)
            return;

        // Onto the UI thread before it touches the dictionary the sweep reads and the nodes it writes.
        Dispatcher.UIThread.Post(() =>
        {
            var now = DateTimeOffset.UtcNow;
            // An out-of-range value is a newer client's state we have no reading for; claiming nothing is the safe
            // answer, and the sample still counts as contact.
            _reportedPresence[sample.CharacterId] = (
                Enum.IsDefined((PresenceState)(int)sample.Value) ? (PresenceState)(int)sample.Value : PresenceState.Unknown,
                now);
            _RefreshPresence(now);
        });
    }

    /// <summary>
    /// Re-read every member's presence. Public and clock-driven so a test owns the time rather than waiting out the
    /// silence window; run on the sweep because a departing client's whole signature is that nothing happens.
    /// </summary>
    public void RefreshPresence(DateTimeOffset now) => _RefreshPresence(now);

    private void _RefreshPresence(DateTimeOffset now)
    {
        foreach (var member in _members)
        {
            if (!_nodesByCharacter.TryGetValue(member.CharacterId, out var nodes))
                continue;

            // The later of the two accounts of contact. The roster read's LastSeenAt is written at most every
            // SeenWriteThrottle and was taken whenever this window last reloaded; a live sample is exact but only
            // covers pilots who have published since it opened. Neither alone answers for every member.
            var reported = _reportedPresence.TryGetValue(member.CharacterId, out var last)
                ? last
                : (State: PresenceState.Unknown, At: (DateTimeOffset?)null);
            var heardAt = (reported.At, member.LastSeenAt) switch
            {
                ({ } live, { } stored) => live > stored ? live : stored,
                ({ } live, null) => live,
                (null, { } stored) => stored,
                _ => (DateTimeOffset?)null,
            };

            var verdict = FleetMemberPresence.Read(
                _presence?.IsInGame(member.CharacterId, NameFor(member.CharacterId)),
                reported.State,
                FleetMemberPresence.IsSilent(heardAt, now));
            foreach (var node in nodes)
                node.Presence = verdict;
        }
    }

    private void _OnRosterChanged(FleetRosterChange change)
    {
        // The echo of what this window just did and has already redrawn.
        int own = _ownAnnouncements.FindIndex(c => ReferenceEquals(c, change));
        if (own >= 0)
        {
            _ownAnnouncements.RemoveAt(own);
            return;
        }

        if (change.FleetId == _fleet.Id)
            _ = ReloadAsync();
    }

    public void Dispose()
    {
        _presenceSweep.Stop();
        _presenceSubscription?.Dispose();
        _metricSubscription?.Dispose();
        _rosterSubscription.Dispose();
    }

    /// <summary>Tell every other open screen that this fleet's roster moved. Announcing is not "also refresh the
    /// neighbours" — it is the one route a roster change travels, the same one the fleet browser and fleet metrics
    /// listen on, so this window never has to know which of them happens to be open (ET-52).</summary>
    private void _Announce(FleetRosterChange change)
    {
        _ownAnnouncements.Add(change);
        _rosterWatch.Announce(change);
    }

    // Every mutation this window makes to the roster ends here: redraw what is on screen, then announce it.
    private async Task _RosterChangedAsync(FleetRosterChange change)
    {
        await ReloadAsync();
        _Announce(change);
    }

    /// <summary>The fleet this window manages — its identity for the module host so each fleet's roster is its own
    /// module (a second fleet's MANAGE opens its own window rather than re-selecting the first one's).</summary>
    public long FleetId => _fleet.Id;

    public ObservableCollection<RosterEntryViewModel> Entries { get; } = [];
    public ObservableCollection<object> Tree { get; } = [];

    /// <summary>The coupled composition's two-level fill overview: one chip per role-group with its
    /// group-minimum tally and the per-fit minima for entries that set one. Empty when no composition is coupled.</summary>
    public ObservableCollection<CompositionFillRoleViewModel> CompositionFill { get; } = [];
    public bool HasCompositionFill => CompositionFill.Count > 0;

    public string FleetName { get; }
    public bool IsOwner { get; }

    // --- Coupled composition band ---

    public bool HasCoupledComposition => _coupledCompositionId is not null;

    public string CoupledCompositionName =>
        _coupledComposition?.Composition.Name ?? (HasCoupledComposition ? "Loading…" : "No composition coupled");

    /// <summary>CHANGE/UNLINK are owner-only and only while the fleet is forming — the doctrine is chosen before start.</summary>
    public bool CanCoupleComposition => _isOwner && _compositions is not null && _fleet.Activation == FleetActivation.Forming;

    [RelayCommand]
    private async Task ChangeComposition()
    {
        if (!CanCoupleComposition)
            return;

        // Couple from the whole server-wide library (ListAll), not just the acting character's own compositions
        // (ListAsync = ListByOwner on a server): a doctrine to couple is a shared-server one, often authored by
        // someone else. On the local store ListAll == the owner's own, so client-only fleets are unaffected.
        IReadOnlyList<FleetCompositionInfo> comps;
        try
        {
            comps = await _compositions!.ListAllAsync();
        }
        catch (FleetTransportException ex)
        {
            // "None to couple" is only true when the read succeeded and came back empty (ET-77).
            StatusMessage = $"Couldn't load the compositions — {ex.Message}";
            return;
        }

        if (comps.Count == 0)
        {
            StatusMessage = "No compositions to couple — create one in the Compositions library first.";
            return;
        }

        // Reuse the generic character-picker (id as the composition id — local/server DB ids fit an int).
        var options = comps.Select(c => new CharacterPickOption((int)c.Id, c.Name, "", Enabled: true)).ToList();
        var picked = await _dialogs.PickCharacterAsync("Couple which composition?", options);
        if (picked is null)
            return;

        var (ok, message) = await _fleets.SetFleetCompositionAsync(_fleet.Id, picked.Value);
        StatusMessage = ok ? "Composition coupled." : $"Couple failed: {message}";
        if (ok)
        {
            _coupledCompositionId = picked.Value;
            await _LoadCoupledCompositionAsync();
            BuildCompositionFill();   // newly coupled doctrine → show its roles (0-filled against the current roster)
        }
    }

    [RelayCommand]
    private async Task UnlinkComposition()
    {
        if (!CanCoupleComposition)
            return;

        var (ok, message) = await _fleets.SetFleetCompositionAsync(_fleet.Id, null);
        StatusMessage = ok ? "Composition unlinked." : $"Unlink failed: {message}";
        if (ok)
        {
            _coupledCompositionId = null;
            _coupledComposition = null;
            OnPropertyChanged(nameof(HasCoupledComposition));
            OnPropertyChanged(nameof(CoupledCompositionName));
            BuildCompositionFill();   // doctrine gone → clear the fill overview
        }
    }

    // --- Coupled in-game fleet band ---

    public bool HasEsiFleet => _esiFleetId is not null;

    /// <summary>COUPLE is owner-only and only shown while not yet coupled — once linked it gives way to the coupled state.</summary>
    public bool CanCoupleEsiFleet => _isOwner && !HasEsiFleet;

    public string EsiCoupleStatus => _esiFleetId is { } id
        ? $"Coupled — in-game fleet {id}" + (_esiFleetBossId is { } boss ? $" (boss {_BossDisplayName(boss)})" : "")
        : "Not coupled";

    // The server relays only the boss's character id; show the resolved name when we have it (filled by ReloadAsync /
    // the in-game band refresh), falling back to the id until then.
    private string _BossDisplayName(int bossCharacterId) =>
        _nameById.TryGetValue(bossCharacterId, out var name) ? name : bossCharacterId.ToString();

    // ESI scope gating: the scope-requiring buttons disable + explain via a tooltip when the character lacks the
    // scope, evaluated locally on load (HasScope, no ESI call). COUPLE/detection needs read_fleet on the acting character;
    // the control row needs write_fleet on the boss. A runtime attempt still raises a toast (the command guards below).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsiReadTooltip))]
    private bool _esiReadAllowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsiReadTooltip))]
    private string? _esiReadScopeReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsiWriteTooltip))]
    private bool _esiWriteAllowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsiWriteTooltip))]
    private string? _esiWriteScopeReason;

    public string? EsiReadTooltip => EsiReadAllowed ? null : EsiReadScopeReason;
    public string? EsiWriteTooltip => EsiWriteAllowed ? null : EsiWriteScopeReason;

    /// <summary>The character this roster is acting as is a member (not the owner) → it can leave from here.
    /// The owner leaves by disbanding/transferring, never with LEAVE.</summary>
    [ObservableProperty] private bool _canLeaveFleet;

    [RelayCommand]
    private async Task CoupleEsiFleet()
    {
        if (!_isOwner)
            return;

        // COUPLE detects the in-game fleet through the acting character, which needs the opt-in Fleet read scope.
        // Check it up-front so a missing scope is a clear toast, not a silent "no fleet detected" buried in the status bar.
        if (!await _EnsureEsiScopeAsync(_actingCharacterId, FleetsScopeCatalog.ReadFleet))
            return;

        if (_services.GetService<IEsiFleetClient>() is not { } esi)
        {
            _toasts.Show("ESI unavailable", "In-game fleet detection is not available in this build.", ToastKind.Error);
            return;
        }

        var detect = await esi.GetCharacterFleetAsync(_actingCharacterId);
        if (!detect.IsSuccess || detect.Value is not { } live)
        {
            _toasts.Show("No in-game fleet detected",
                "Form a fleet in EVE first (Form Fleet) — EVE Together then detects it through your character.", ToastKind.Warning);
            return;
        }

        var (ok, message) = await _fleets.CoupleFleetToEsiAsync(_fleet.Id, live.FleetId, live.FleetBossId);
        if (!ok)
        {
            _toasts.Show("Couple failed", message ?? "Could not couple to the in-game fleet.", ToastKind.Error);
            return;
        }

        _esiFleetId = live.FleetId;
        _esiFleetBossId = live.FleetBossId;
        OnPropertyChanged(nameof(HasEsiFleet));
        OnPropertyChanged(nameof(CanCoupleEsiFleet)); // COUPLE gives way to the coupled state
        OnPropertyChanged(nameof(EsiCoupleStatus));
        await _EvaluateEsiScopesAsync(); // the control row's write_fleet gating + the boss name, now that the fleet is linked
        _toasts.Show("Coupled", $"Linked to your in-game fleet (boss {live.FleetBossId}).", ToastKind.Success);
    }

    /// <summary>Owner-only manual uncouple: drops the stored in-game link so the app stops driving / polling ESI
    /// for this fleet. Storage-role only — no ESI call. (The poller also uncouples automatically once the in-game fleet
    /// is gone for a few polls.)</summary>
    [RelayCommand]
    private async Task UncoupleEsiFleet()
    {
        if (!_isOwner || !HasEsiFleet)
            return;

        var (ok, message) = await _fleets.UncoupleFleetFromEsiAsync(_fleet.Id);
        if (!ok)
        {
            _toasts.Show("Uncouple failed", message ?? "Could not uncouple from the in-game fleet.", ToastKind.Error);
            return;
        }

        _esiFleetId = null;
        _esiFleetBossId = null;
        OnPropertyChanged(nameof(HasEsiFleet));
        OnPropertyChanged(nameof(CanCoupleEsiFleet)); // the coupled state gives way back to COUPLE
        OnPropertyChanged(nameof(EsiCoupleStatus));
        await _EvaluateEsiScopesAsync();
        _toasts.Show("Uncoupled", "Cleared the in-game fleet link.", ToastKind.Success);
    }

    // --- ESI fleet control: MOTD/free-move, structure push, roster invite + write-scope gating ---

    [ObservableProperty] private string _esiMotd = "";
    [ObservableProperty] private bool _esiFreeMove;

    // --- ESI automation toggles. Auto Apply pushes a newly added wing/squad to the in-game fleet as it is
    // created; Auto Invite sends an in-game invite to a member the moment they are assigned into structure. Both are
    // owner-set per-fleet preferences, persisted server-side (storage-role) so they survive a restart; they only take
    // effect while the fleet is coupled and the boss holds write_fleet, and the manual PUSH STRUCTURE / INVITE ROSTER
    // stay available regardless. Additive only — removing in-game units stays behind the manual confirm. ---

    [ObservableProperty] private bool _esiAutoApplyStructure;
    [ObservableProperty] private bool _esiAutoInviteMembers;

    partial void OnEsiAutoApplyStructureChanged(bool value) => _ = _SaveEsiAutomationAsync();
    partial void OnEsiAutoInviteMembersChanged(bool value) => _ = _SaveEsiAutomationAsync();

    // Persists both toggles together (owner-only); a save failure is surfaced as a toast. No revert-on-failure: setting
    // the property back would re-enter this save, and the setting is owner-gated and low-stakes.
    private async Task _SaveEsiAutomationAsync()
    {
        if (!_isOwner)
            return;

        var (ok, message) = await _fleets.SetFleetEsiAutomationAsync(_fleet.Id, EsiAutoApplyStructure, EsiAutoInviteMembers);
        if (!ok)
            _toasts.Show("Could not save", message ?? "The ESI automation setting was not saved.", ToastKind.Error);
    }

    // Auto-pushes the additive structure (new wings/squads) to the live fleet after a structure add, when Auto Apply is
    // on. Reuses the move/rename mirror gating (coupled + write_fleet); ApplyFleetStructureAsync is idempotent so it only
    // creates what is missing. Silent on the not-coupled / scope-missing case; a real push failure surfaces via the helper.
    private async Task _AutoApplyStructureAsync()
    {
        if (!EsiAutoApplyStructure)
            return;

        await _MirrorRosterChangeToEsiAsync((control, esiFleetId, bossCharacterId) =>
            control.ApplyFleetStructureAsync(_fleets, _fleet.Id, esiFleetId, bossCharacterId));
    }

    // Reflects an assigned structure position in the live fleet via the control service, which reads in-game presence
    // first and MOVES a present pilot or INVITES an absent one — never a doomed move on a non-member (the source of the
    // "Cannot move non-member" 400). Gated on coupled + write_fleet and a real position (never an unassign). On the
    // move/drag paths <paramref name="invite"/> is the Auto Invite toggle; the explicit "Invite here" action forces it
    // true. A real move failure (present pilot) surfaces; the invite is best-effort/silent.
    private async Task _SyncMemberPositionToEsiAsync(int characterId, FleetRole role, long wingId, long squadId, bool invite)
    {
        if (!HasEsiFleet || !EsiWriteAllowed || role == FleetRole.Unassigned
            || _services.GetService<FleetEsiControlService>() is not { } control
            || _esiFleetId is not { } esiFleetId || _esiFleetBossId is not { } bossCharacterId)
            return;

        var result = await control.SyncMemberPositionAsync(
            _fleets, _fleet.Id, esiFleetId, bossCharacterId, characterId, role, wingId, squadId, invite);
        if (!result.IsSuccess)
            _ReportEsiFailure(result.Messages);
    }

    [RelayCommand]
    private async Task ApplyFleetSettings()
    {
        if (!await _EnsureEsiWriteAsync() || _services.GetService<FleetEsiControlService>() is not { } control
            || _esiFleetId is not { } esiFleetId || _esiFleetBossId is not { } bossCharacterId)
            return;

        // A blank field leaves the in-game MOTD unchanged rather than clearing it; the toggle is always sent.
        var motd = string.IsNullOrWhiteSpace(EsiMotd) ? null : EsiMotd;
        _ReportEsiResult(
            await control.SetFleetSettingsAsync(_fleet.Id, esiFleetId, bossCharacterId, motd, EsiFreeMove),
            "MOTD and free-move applied to the in-game fleet.");
    }

    [RelayCommand]
    private async Task PushFleetStructure()
    {
        if (!await _EnsureEsiWriteAsync() || _services.GetService<FleetEsiControlService>() is not { } control
            || _esiFleetId is not { } esiFleetId || _esiFleetBossId is not { } bossCharacterId)
            return;

        var pushed = await control.ApplyFleetStructureAsync(_fleets, _fleet.Id, esiFleetId, bossCharacterId);
        _ReportEsiResult(pushed, "Wing/squad structure pushed to the in-game fleet.");
        if (!pushed.IsSuccess)
            return;

        // Destructive half: remove in-game wings/squads no longer in the plan — preview, confirm with the
        // exact list, then delete. Only empty units are removed and the EVE defaults are protected (the service guards it).
        var obsolete = await control.DeleteObsoleteInGameUnitsAsync(_fleets, _fleet.Id, esiFleetId, bossCharacterId, dryRun: true);
        if (!obsolete.IsSuccess || obsolete.Value!.Count == 0)
            return;

        if (!await _dialogs.ConfirmAsync("Remove from in-game fleet?",
                "These wings/squads are no longer in your plan and will be removed from the in-game fleet:\n• "
                + string.Join("\n• ", obsolete.Value) + "\n\nMembers are kept — only empty units are removed.", okText: "Remove"))
            return;

        var removed = await control.DeleteObsoleteInGameUnitsAsync(_fleets, _fleet.Id, esiFleetId, bossCharacterId, dryRun: false);
        if (!removed.IsSuccess)
        {
            _ReportEsiFailure(removed.Messages);
            return;
        }

        var removeWarnings = removed.Messages.Where(message => message.Severity == MessageSeverity.Warning).ToList();
        _toasts.Show(removeWarnings.Count == 0 ? "In-game fleet updated" : "In-game fleet updated with warnings",
            $"Removed {removed.Value!.Count} obsolete wing/squad(s) from the in-game fleet."
            + (removeWarnings.Count == 0 ? "" : " " + string.Join(" ", removeWarnings.Select(message => message.Text))),
            removeWarnings.Count == 0 ? ToastKind.Success : ToastKind.Warning);
    }

    [RelayCommand]
    private async Task InviteRoster()
    {
        if (!await _EnsureEsiWriteAsync() || _services.GetService<FleetEsiControlService>() is not { } control
            || _esiFleetId is not { } esiFleetId || _esiFleetBossId is not { } bossCharacterId)
            return;

        var result = await control.InviteRosterAsync(_fleets, _fleet.Id, esiFleetId, bossCharacterId);
        if (!result.IsSuccess)
        {
            _ReportEsiFailure(result.Messages);
            return;
        }

        var invited = result.Value!.Count(outcome => outcome.Status == EsiInviteStatus.Invited);
        var failed = result.Value!.Where(outcome => outcome.Status == EsiInviteStatus.Failed).ToList();
        if (failed.Count == 0)
            _toasts.Show("Roster invited", $"Sent {invited} invite(s) to the in-game fleet.", ToastKind.Success);
        else
            _toasts.Show("Roster invited with issues",
                $"{invited} invited, {failed.Count} could not be invited: {string.Join("; ", failed.Select(outcome => outcome.Message))}",
                ToastKind.Warning);
    }

    // Refreshes the proactive scope gating: COUPLE (read_fleet on the acting character) and the control row (write_fleet
    // on the boss) so they disable + explain through a tooltip before any click. Local HasScope check, no ESI call.
    private async Task _EvaluateEsiScopesAsync()
    {
        var gate = _services.GetService<IEsiScopeGate>();

        // The band shows the boss's name (the server relays only the id) — resolve once, best-effort, cached.
        if (HasEsiFleet && _esiFleetBossId is { } boss && !_nameById.ContainsKey(boss))
        {
            var info = await _lookup.LookupAsync(boss);
            _nameById[boss] = info.Exists ? info.Name : $"Char {boss}";
            OnPropertyChanged(nameof(EsiCoupleStatus));
        }

        var read = gate is null ? null : await gate.EvaluateAsync(_actingCharacterId, [FleetsScopeCatalog.ReadFleet]);
        EsiReadAllowed = read?.IsAllowed ?? false;
        EsiReadScopeReason = read?.Reason ?? "ESI integration is not available in this build.";

        if (gate is null || !HasEsiFleet || _esiFleetBossId is not { } bossCharacterId)
        {
            EsiWriteAllowed = false;
            EsiWriteScopeReason = gate is null ? "ESI integration is not available in this build." : null;
            return;
        }

        var write = await gate.EvaluateAsync(bossCharacterId, [FleetsScopeCatalog.WriteFleet]);
        EsiWriteAllowed = write.IsAllowed;
        EsiWriteScopeReason = write.Reason;
    }

    // Confirms the coupled fleet's boss holds write_fleet before an in-game control action (toasting the reason if not).
    private async Task<bool> _EnsureEsiWriteAsync() =>
        HasEsiFleet && _esiFleetBossId is { } bossCharacterId && await _EnsureEsiScopeAsync(bossCharacterId, FleetsScopeCatalog.WriteFleet);

    // Verifies an ESI scope on a character up-front (local HasScope check, no ESI call) and, when missing, raises a clear
    // toast explaining which scope to grant — ESI feedback goes through toasts, not the status bar.
    private async Task<bool> _EnsureEsiScopeAsync(int characterId, string scope)
    {
        if (_services.GetService<IEsiScopeGate>() is not { } gate)
        {
            _toasts.Show("ESI unavailable", "ESI integration is not available in this build.", ToastKind.Error);
            return false;
        }

        var state = await gate.EvaluateAsync(characterId, [scope]);
        if (state.IsAllowed)
            return true;

        _toasts.Show("ESI access required", state.Reason ?? "A required ESI scope has not been granted for this character.", ToastKind.Error);
        return false;
    }

    // Surfaces an ESI control outcome as a toast: a success confirmation (carrying any warnings), or the failure reason —
    // including a missing/forbidden scope, so the gate UX holds at runtime, not only on load.
    private void _ReportEsiResult(Result result, string successText)
    {
        if (!result.IsSuccess)
        {
            _ReportEsiFailure(result.Messages);
            return;
        }

        var warnings = result.Messages.Where(message => message.Severity == MessageSeverity.Warning).ToList();
        if (warnings.Count == 0)
            _toasts.Show("In-game fleet updated", successText, ToastKind.Success);
        else
            _toasts.Show("In-game fleet updated with warnings",
                $"{successText} {string.Join(" ", warnings.Select(message => message.Text))}", ToastKind.Warning);
    }

    private void _ReportEsiFailure(IReadOnlyList<ResultMessage> messages) =>
        _toasts.Show("ESI action failed",
            messages.Count > 0 ? string.Join(" ", messages.Select(message => message.Text)) : "The ESI action could not be completed.",
            ToastKind.Error);

    // Mirrors an internal roster move/kick to the live in-game fleet when the fleet is coupled and the boss holds
    // write_fleet. Best-effort: stays silent on success (the in-game fleet just follows) and when not coupled / the scope
    // is missing (the manage band already gates that), but surfaces a real push failure (structure not pushed yet, the
    // pilot isn't in the in-game fleet, the scope was lost) as a toast so the FC knows the in-game side didn't move.
    private async Task _MirrorRosterChangeToEsiAsync(Func<FleetEsiControlService, long, int, Task<Result>> push)
    {
        if (!HasEsiFleet || !EsiWriteAllowed
            || _services.GetService<FleetEsiControlService>() is not { } control
            || _esiFleetId is not { } esiFleetId || _esiFleetBossId is not { } bossCharacterId)
            return;

        var result = await push(control, esiFleetId, bossCharacterId);
        if (!result.IsSuccess)
            _ReportEsiFailure(result.Messages);
    }

    private async Task _LoadCoupledCompositionAsync()
    {
        // Re-read the coupling from the backing store so a remote couple/unlink (pushed as fleet.changed →
        // CompositionChanged) updates the band for a viewer — not just for the owner who changed it locally.
        if (_compositions is not null && await _fleets.GetFleetAsync(_fleet.Id) is { } current)
            _coupledCompositionId = current.FleetCompositionId;

        _coupledComposition = _coupledCompositionId is long id && _compositions is not null
            ? await _compositions.GetAsync(id)
            : null;
        OnPropertyChanged(nameof(HasCoupledComposition));
        OnPropertyChanged(nameof(CoupledCompositionName));
    }

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _activationLabel = "";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canConclude;

    /// <summary>STOP is offered on an active fleet the acting character owns — the way back to standing by (ET-166).
    /// Same condition as <see cref="CanConclude"/> on purpose: they are the two exits from one state, and the choice
    /// between them is made in the stop dialog rather than by two buttons of equal weight in the header.</summary>
    [ObservableProperty] private bool _canStop;

    private void UpdateActivationLabel(FleetActivation activation)
    {
        ActivationLabel = activation switch
        {
            FleetActivation.Active => "Active",
            FleetActivation.Concluded => "Concluded",
            _ => "Forming"
        };
        CanStart = _isOwner && activation == FleetActivation.Forming;
        // CONCLUDE only applies to an Active fleet (the op ran); a Forming fleet is cancelled via Disband, and a
        // Concluded one is terminal.
        CanConclude = _isOwner && activation == FleetActivation.Active;
        CanStop = CanConclude;
    }

    [RelayCommand]
    private Task Refresh() => ReloadAsync();

    /// <summary>Leaves this fleet with one or more of my characters — self-removal, distinct from the owner-only
    /// remove. With several of my characters in the fleet (multi-box) a picker asks which to pull out; with one it just
    /// confirms. The owner's own character is never a candidate (it disbands/transfers).</summary>
    [RelayCommand]
    private async Task LeaveFleet()
    {
        var leavable = _leavableCharacters;
        if (leavable.Count == 0)
            return;

        List<int> toLeave;
        if (leavable.Count == 1)
        {
            if (!await _dialogs.ConfirmAsync("Leave fleet", $"Leave '{FleetName}' with {leavable[0].Name}?", okText: "Leave"))
                return;
            toLeave = [leavable[0].Id];
        }
        else
        {
            var options = leavable
                .Select(c => new CharacterPickOption(c.Id, c.Name, "in fleet", Enabled: true))
                .ToList();
            var picked = await _dialogs.PickCharactersAsync($"Leave '{FleetName}' with which character(s)?", options);
            if (picked is null || picked.Count == 0)
                return;
            toLeave = picked.ToList();
        }

        var nameById = leavable.ToDictionary(c => c.Id, c => c.Name);
        var leftNames = new List<string>();
        var leftIds = new List<int>();
        string? lastError = null;
        foreach (var characterId in toLeave)
        {
            var left = await _fleets.LeaveFleetAsync(_fleet.Id, characterId);
            if (left.Ok)
            {
                leftNames.Add(nameById.GetValueOrDefault(characterId, $"char {characterId}"));
                leftIds.Add(characterId);
            }
            else
                lastError = left.Message;
        }

        if (leftNames.Count > 0)
        {
            _toasts.Show($"Left '{FleetName}'", string.Join(", ", leftNames));
            if (_onActivationChanged is not null)
                await _onActivationChanged();   // membership changed → refresh the parent overview
            await ReloadAsync();                 // the roster reflects the leave (candidates shrink; LEAVE hides at zero)

            // A character leaving is that character removed from the fleet, so it travels the same route as a kick:
            // their row goes off fleet metrics and the browser card too, one announcement per character that left.
            foreach (int characterId in leftIds)
                _Announce(FleetRosterChange.Removed(_fleet.Id, characterId));
        }
        if (lastError is not null)
            _toasts.Show("Leave failed",
                string.IsNullOrWhiteSpace(lastError) ? $"Could not leave '{FleetName}'." : lastError, ToastKind.Error);
    }

    private async Task ReloadAsync()
    {
        await _LoadCoupledCompositionAsync();   // the band name + the picker's composition scope
        await _EvaluateEsiScopesAsync();         // proactive disable + tooltip for the scope-requiring ESI buttons

        var members = await _fleets.ListMembersAsync(_fleet.Id);
        _members = members;

        // LEAVE candidates: each of MY characters in the fleet except its owner. The owner's own
        // character leaves by disbanding/transferring, never with LEAVE.
        var memberIds = members.Select(m => m.CharacterId).ToHashSet();
        _leavableCharacters = (await _fleets.ListConnectedCharactersAsync())
            .Where(c => memberIds.Contains(c.CharacterId) && c.CharacterId != _fleet.CreatorCharacterId)
            .Select(c => (c.CharacterId, c.CharacterName))
            .ToList();
        CanLeaveFleet = _leavableCharacters.Count > 0;

        var invites = _isOwner ? await _fleets.ListPendingFleetInvitesAsync(_fleet.Id) : [];
        var requests = _isOwner ? await _fleets.ListPendingJoinRequestsAsync(_fleet.Id) : [];

        // Resolve names for EVERY character shown in the roster — accepted members AND pending invitees AND
        // join-requesters — so a pending external pilot shows their name instead of "Char <id>".
        await ResolveNamesAsync(members.Select(m => m.CharacterId)
            .Concat(invites.Select(i => i.InviteeCharacterId))
            .Concat(requests.Select(r => r.RequesterCharacterId)));

        // The tree is built from the actual wing/squad structure (not just placed members), so freshly added empty
        // wings/squads appear — and an empty wing can then be selected to add a squad to.
        var wings = await _fleets.ListWingsAsync(_fleet.Id);
        var squadsByWing = new Dictionary<long, IReadOnlyList<FleetSquadInfo>>();
        foreach (var wing in wings)
            squadsByWing[wing.Id] = await _fleets.ListSquadsAsync(wing.Id);

        await ComputeSkillBadgesAsync(members);   // can-fly verdicts, read before the nodes are built
        await ReportOwnVerdictAsync(members);     // cross-client: push my own verdict when it changed
        // Both builders below mint fresh member nodes; the sweep's index has to start empty or it would go on writing
        // to the nodes of a roster that is no longer on screen.
        _nodesByCharacter.Clear();
        BuildList(members, invites, requests, wings, squadsByWing);
        BuildTree(members, wings, squadsByWing);
        _RefreshPresence(DateTimeOffset.UtcNow);   // the new nodes start at Unknown; seed them before they are seen
        BuildCompositionFill();   // fresh composition (above) + fresh roster → the two-level fill
    }

    /// <summary>Resolves the can-fly badge for every member with an assigned fit, off their cached skills
    /// (no ESI import). A member with no fit, no locally known skills, or when the validator/SDE is unavailable gets no
    /// entry — the row then shows no badge rather than a misleading "can't fly".</summary>
    private async Task ComputeSkillBadgesAsync(IReadOnlyList<FleetMemberInfo> members)
    {
        if (_skillEvaluator is null)
        {
            _skillBadges = new Dictionary<long, MemberSkillBadge>();
            return;
        }

        var badges = new Dictionary<long, MemberSkillBadge>();
        foreach (var member in members)
            if (await _skillEvaluator.EvaluateAsync(member.CharacterId, member.AssignedFit) is { } badge)
                badges[member.Id] = badge;
        _skillBadges = badges;
    }

    /// <summary>cross-client: this client is the skill authority for the acting character, so it pushes
    /// the locally computed verdict for their own member row when it differs from the stored wire verdict — the
    /// pilot may have only this window open. Best-effort and idempotent on the stored value (no report loop); a
    /// missing local badge is never reported, so unknown cannot overwrite a real verdict.</summary>
    private async Task ReportOwnVerdictAsync(IReadOnlyList<FleetMemberInfo> members)
    {
        var mine = members.FirstOrDefault(m => m.CharacterId == _actingCharacterId);
        if (mine?.AssignedFit is null || !_skillBadges.TryGetValue(mine.Id, out var badge))
            return;

        var verdict = badge.CanFly ? FitSkillVerdict.CanFly : FitSkillVerdict.MissingSkills;
        if (mine.FitSkillVerdict == verdict)
            return;

        try { await _fleets.ReportMemberFitVerdictAsync(mine.Id, verdict); }
        catch { /* best-effort — the verdict travels on a later reload */ }
    }

    private async Task ResolveNamesAsync(IEnumerable<int> characterIds)
    {
        foreach (var character in await _fleets.ListConnectedCharactersAsync())
            _nameById[character.CharacterId] = character.CharacterName;

        // Anyone not in the live connected set (offline members, externals, pending invitees/requesters) is resolved
        // best-effort via public ESI (R3-2). The lookup caches names for a day, and _nameById persists across reloads,
        // so this runs once per character — a real EVE pilot gets their name; an id ESI does not know (e.g. a synthetic
        // dev id) falls back to "Char <id>".
        foreach (var characterId in characterIds.Distinct().Where(id => !_nameById.ContainsKey(id)))
        {
            var info = await _lookup.LookupAsync(characterId);
            _nameById[characterId] = info.Exists ? info.Name : $"Char {characterId}";
        }
    }

    private string NameFor(int characterId) =>
        _nameById.TryGetValue(characterId, out var name) ? name : $"Char {characterId}";

    private string CommanderSuffix(FleetMemberInfo? commander, string roleAbbrev) =>
        commander is null ? string.Empty : $" · {roleAbbrev}: {NameFor(commander.CharacterId)}";

    private void BuildList(
        IReadOnlyList<FleetMemberInfo> members,
        IReadOnlyList<FleetInviteInfo> invites,
        IReadOnlyList<FleetJoinRequestInfo> requests,
        IReadOnlyList<FleetWingInfo> wings,
        IReadOnlyDictionary<long, IReadOnlyList<FleetSquadInfo>> squadsByWing)
    {
        // Each accepted row carries a full member node so the left list offers the same manage menu as the tree —
        // it is the ONLY manage surface for an unplaced member, who has no structure node (see BuildTree).
        Entries.Clear();
        foreach (var member in members)
            Entries.Add(RosterEntryViewModel.Accepted(
                member, NameFor(member.CharacterId), MemberNode(member, wings, squadsByWing)));
        foreach (var invite in invites)
            Entries.Add(RosterEntryViewModel.PendingInvite(NameFor(invite.InviteeCharacterId)));
        foreach (var request in requests)
            Entries.Add(RosterEntryViewModel.JoinRequest(request.Id, NameFor(request.RequesterCharacterId)));
    }

    /// <summary>
    /// Rebuilds the two-level fill overview from the coupled composition and the current roster: for each
    /// role-group, how many members fly a fit that fills it (group minimum, e.g. "DPS 2/40") and, per entry that sets a
    /// per-fit minimum, how many fly that exact fit (e.g. "Guardian 1/3"). The join is the member's
    /// <see cref="FleetMemberInfo.AssignedCompositionEntryId"/> — the same doctrine-entry tag the assign flow records —
    /// so members flying an own fit outside the doctrine (no entry id) do not count toward any minimum. A role with
    /// neither a group minimum nor any per-fit minimum is omitted (nothing to fill against).
    /// </summary>
    private void BuildCompositionFill()
    {
        CompositionFill.Clear();
        foreach (var fill in CompositionFillBuilder.Build(_coupledComposition, _members))
            CompositionFill.Add(fill);
        OnPropertyChanged(nameof(HasCompositionFill));
    }

    // --- E1: EVE tree with the commander of each level in its node header, the rest of the members underneath. ---

    private void BuildTree(
        IReadOnlyList<FleetMemberInfo> members,
        IReadOnlyList<FleetWingInfo> wings,
        IReadOnlyDictionary<long, IReadOnlyList<FleetSquadInfo>> squadsByWing)
    {
        Tree.Clear();

        var fc = members.FirstOrDefault(m => m.WingId < 0 && m.Role == FleetRole.FleetCommander);

        // An unplaced pilot (fleet level without being the FC — unassigned, or a fresh external) is NOT a structure
        // node: they live in the left MEMBERS list until the owner places them on a wing/squad. The root count
        // matches what the tree shows, so it counts placed members only.
        var placedCount = members.Count(m => m.WingId >= 0) + (fc is null ? 0 : 1);

        var fleetRoot = new FleetRootNodeViewModel(
            label: $"Fleet ({placedCount}){CommanderSuffix(fc, "FC")}",
            isOwner: _isOwner,
            addWingCommand: Cmd(AddWingAsync),
            inviteHereCommand: Cmd(() => InviteToPositionAsync(-1, -1)),
            assignHereCommand: Cmd(() => AssignAcceptedToPositionAsync(FleetRole.FleetCommander, -1, -1)),
            commander: fc is null ? null : MemberNode(fc, wings, squadsByWing));

        foreach (var wing in wings.OrderBy(w => w.Id))
        {
            var wingMembers = members.Where(m => m.WingId == wing.Id).ToList();
            var wc = wingMembers.FirstOrDefault(m => m.SquadId < 0 && m.Role == FleetRole.WingCommander);
            var squads = squadsByWing.TryGetValue(wing.Id, out var list) ? list : [];

            var wingNode = new WingNodeViewModel(
                wingId: wing.Id,
                label: $"{wing.Name} ({wingMembers.Count}){CommanderSuffix(wc, "WC")}",
                isOwner: _isOwner,
                addSquadCommand: Cmd(() => AddSquadAsync(wing.Id, wing.Name)),
                inviteHereCommand: Cmd(() => InviteToPositionAsync(wing.Id, -1)),
                assignHereCommand: Cmd(() => AssignAcceptedToPositionAsync(FleetRole.WingCommander, wing.Id, -1)),
                renameCommand: Cmd(() => RenameWingAsync(wing.Id, wing.Name)),
                deleteCommand: Cmd(() => DeleteWingAsync(wing.Id, wing.Name)),
                canDelete: squads.Count == 0 && wingMembers.Count == 0,
                commander: wc is null ? null : MemberNode(wc, wings, squadsByWing));

            foreach (var squad in squads.OrderBy(s => s.Id))
            {
                var squadMembers = wingMembers.Where(m => m.SquadId == squad.Id).ToList();
                var sc = squadMembers.FirstOrDefault(m => m.Role == FleetRole.SquadCommander);

                var squadNode = new SquadNodeViewModel(
                    squadId: squad.Id,
                    wingId: wing.Id,
                    label: $"{squad.Name} ({squadMembers.Count}){CommanderSuffix(sc, "SC")}",
                    isOwner: _isOwner,
                    inviteHereCommand: Cmd(() => InviteToPositionAsync(wing.Id, squad.Id)),
                    assignHereCommand: Cmd(() => AssignAcceptedToPositionAsync(FleetRole.SquadMember, wing.Id, squad.Id)),
                    renameCommand: Cmd(() => RenameSquadAsync(squad.Id, wing.Name, squad.Name)),
                    deleteCommand: Cmd(() => DeleteSquadAsync(squad.Id, squad.Name)),
                    canDelete: squadMembers.Count == 0,
                    commander: sc is null ? null : MemberNode(sc, wings, squadsByWing));

                foreach (var member in squadMembers.Where(m => m.Role != FleetRole.SquadCommander))
                    squadNode.Members.Add(MemberNode(member, wings, squadsByWing));

                wingNode.Children.Add(squadNode);
            }

            // Wing-level members (squad id -1) other than the wing commander shown in the header.
            foreach (var member in wingMembers.Where(m => m.SquadId < 0 && m.Role != FleetRole.WingCommander))
                wingNode.Children.Add(MemberNode(member, wings, squadsByWing));

            fleetRoot.Children.Add(wingNode);
        }

        Tree.Add(fleetRoot);
    }

    private MemberNodeViewModel MemberNode(
        FleetMemberInfo member,
        IReadOnlyList<FleetWingInfo> wings,
        IReadOnlyDictionary<long, IReadOnlyList<FleetSquadInfo>> squadsByWing)
    {
        var node = new MemberNodeViewModel(member, NameFor(member.CharacterId), _isOwner,
            BuildMoveTargets(member, wings, squadsByWing),
            unassignCommand: Cmd(() => UnassignMemberAsync(member)),
            removeFromFleetCommand: Cmd(() => RemoveMemberAsync(member)),
            transferOwnershipCommand: Cmd(() => TransferOwnershipAsync(member)),
            assignFitCommand: Cmd(() => AssignFitAsync(member)),
            openFitCommand: Cmd(() => OpenMemberFitAsync(member)),
            skillBadge: _skillBadges.GetValueOrDefault(member.Id),
            shipName: member.AssignedFit is { } fit ? _shipNames.TypeName(fit.ShipTypeId) : null);

        if (!_nodesByCharacter.TryGetValue(member.CharacterId, out var nodes))
            _nodesByCharacter[member.CharacterId] = nodes = [];
        nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Builds the EVE move-cascade for one member: Fleet Commander · per wing → Wing Commander · per squad → Squad
    /// Commander / Squad Member. Each leaf carries a command bound to (this member, that exact role+wing+squad). Built
    /// per member because the leaf command closes over the member — cheap at fleet scale (≤ 5 wings × 5 squads).
    /// </summary>
    private IReadOnlyList<MoveTargetViewModel> BuildMoveTargets(
        FleetMemberInfo member,
        IReadOnlyList<FleetWingInfo> wings,
        IReadOnlyDictionary<long, IReadOnlyList<FleetSquadInfo>> squadsByWing)
    {
        var targets = new List<MoveTargetViewModel>
        {
            Leaf("Fleet Commander", () => MoveMemberToAsync(member, FleetRole.FleetCommander, -1, -1))
        };

        foreach (var wing in wings.OrderBy(w => w.Id))
        {
            var wingChildren = new List<MoveTargetViewModel>
            {
                Leaf("Wing Commander", () => MoveMemberToAsync(member, FleetRole.WingCommander, wing.Id, -1))
            };

            var squads = squadsByWing.TryGetValue(wing.Id, out var list) ? list : [];
            foreach (var squad in squads.OrderBy(s => s.Id))
            {
                wingChildren.Add(new MoveTargetViewModel(squad.Name,
                    [
                        Leaf("Squad Commander", () => MoveMemberToAsync(member, FleetRole.SquadCommander, wing.Id, squad.Id)),
                        Leaf("Squad Member", () => MoveMemberToAsync(member, FleetRole.SquadMember, wing.Id, squad.Id))
                    ],
                    command: null));
            }

            targets.Add(new MoveTargetViewModel(wing.Name, wingChildren, command: null));
        }

        return targets;
    }

    private static MoveTargetViewModel Leaf(string label, Func<Task> action) =>
        new(label, [], new AsyncRelayCommand(action));

    private static IAsyncRelayCommand Cmd(Func<Task> action) => new AsyncRelayCommand(action);

    // --- E2 actions (owner-only; the server is the real authority and its message is surfaced). ---

    private async Task MoveMemberToAsync(FleetMemberInfo member, FleetRole role, long wingId, long squadId)
    {
        if (!_isOwner)
            return;

        var moved = await _fleets.MoveMemberAsync(member.Id, role, wingId, squadId);
        StatusMessage = moved.Ok ? "Member moved." : $"Move failed: {moved.Message}";
        if (!moved.Ok)
            return;

        await _SyncMemberPositionToEsiAsync(member.CharacterId, role, wingId, squadId, EsiAutoInviteMembers);
        await _RosterChangedAsync(FleetRosterChange.Changed(_fleet.Id, member.CharacterId));
    }

    // --- G-3: roster drag-and-drop. The drop target is the node VM under the cursor; resolving it decides whether the
    // drop moves the member to that position or swaps them with the member already in an occupied commander slot.
    // The dragged id comes from a tree member row or the left accepted-member list — both are real FleetMembers, so a
    // drop is always a move/swap, never a fresh assignment. The right-click cascade stays as the precise alternative. ---

    /// <summary>Applies a roster drag-drop: moves the dragged member onto the dropped position, or swaps them with the
    /// member occupying an occupied commander slot. Owner-only; the same path serves a server and a client-only
    /// fleet via the <see cref="IFleetClient"/> seam (G-1 engine + G-2 transport). The dragged id comes from a tree
    /// member row or the left accepted-member list — both are real FleetMembers, so a drop is always a move/swap, never
    /// a fresh assignment. The right-click cascade stays as the precise alternative.</summary>
    public async Task HandleDropAsync(long draggedMemberId, object? targetNode)
    {
        if (!_isOwner || _members.FirstOrDefault(m => m.Id == draggedMemberId) is not { } dragged)
            return;

        var resolution = RosterDropResolver.Resolve(dragged, targetNode);
        switch (resolution.Action)
        {
            case RosterDropAction.Move:
                var moved = await _fleets.MoveMemberAsync(draggedMemberId, resolution.Role, resolution.WingId, resolution.SquadId);
                StatusMessage = moved.Ok ? "Member moved." : $"Move failed: {moved.Message}";
                if (moved.Ok)
                {
                    await _SyncMemberPositionToEsiAsync(dragged.CharacterId, resolution.Role, resolution.WingId, resolution.SquadId, EsiAutoInviteMembers);
                    await _RosterChangedAsync(FleetRosterChange.Changed(_fleet.Id, dragged.CharacterId));
                }
                break;
            case RosterDropAction.Swap:
                // A swap is two simultaneous moves with no clean single-member ESI mapping, so it is not mirrored to the
                // in-game fleet; the FC re-applies the in-game positions via the move cascade.
                var swapped = await _fleets.SwapMembersAsync(draggedMemberId, resolution.OtherMemberId);
                StatusMessage = swapped.Ok ? "Members swapped." : $"Swap failed: {swapped.Message}";
                if (swapped.Ok)
                    // Two members moved at once, so this names neither: everything showing this roster re-reads it.
                    await _RosterChangedAsync(FleetRosterChange.Reloaded(_fleet.Id));
                break;
        }
    }

    /// <summary>Assigns the fit this member flies: the single fit picker, pre-marking the current assignment,
    /// then persists via the fleet client and reloads. Owner-only (the server is the real authority).</summary>
    private async Task AssignFitAsync(FleetMemberInfo member)
    {
        if (!_isOwner)
            return;

        // Scoped to the fleet's coupled composition so the doctrine's fits are the default source; null = full
        // library. The target character drives the per-row can-fly badge.
        var picker = new FitPickerViewModel(_services, FitPickerMode.Single, alreadyAdded: null,
            composition: _coupledComposition, currentFitHash: member.AssignedFit?.ContentHash,
            skillCheckCharacterId: member.CharacterId);

        var fit = await _dialogs.PickFitAsync(picker);
        if (fit is null)
            return;

        // Tag the assignment with the composition entry it fills, if the picked fit came from the doctrine.
        var entryId = _coupledComposition?.Roles.SelectMany(r => r.Entries)
            .FirstOrDefault(e => string.Equals(e.Fit.ContentHash, fit.ContentHash, StringComparison.OrdinalIgnoreCase))?.Id;

        var assigned = await _fleets.AssignMemberFitAsync(member.Id, fit, entryId);
        StatusMessage = assigned.Ok ? $"Assigned {fit.FitName}." : $"Assign failed: {assigned.Message}";
        if (assigned.Ok)
            // The ship and fit a pilot flies show on the browser card's leaf and in the shared member menu on every
            // screen, so an assignment is a change to that member wherever they are drawn.
            await _RosterChangedAsync(FleetRosterChange.Changed(_fleet.Id, member.CharacterId));
    }

    /// <summary>Opens the read-only radial fit-detail of a member's assigned fit — for everyone, not
    /// just the owner.</summary>
    private async Task OpenMemberFitAsync(FleetMemberInfo member)
    {
        if (member.AssignedFit is not null)
            await FitDetailLauncher.OpenAsync(_services, _dialogs, member.AssignedFit);
    }

    /// <summary>"Remove from squad" (R3-5): drops the member to unassigned (fleet level, no wing/squad) — they
    /// stay in the fleet and on the left list, ready to be re-placed. Non-destructive, so no confirm.</summary>
    private async Task UnassignMemberAsync(FleetMemberInfo member)
    {
        if (!_isOwner)
            return;

        var moved = await _fleets.MoveMemberAsync(member.Id, FleetRole.Unassigned, -1, -1);
        StatusMessage = moved.Ok ? "Member removed from squad (unassigned)." : $"Unassign failed: {moved.Message}";
        if (moved.Ok)
            await _RosterChangedAsync(FleetRosterChange.Changed(_fleet.Id, member.CharacterId));
    }

    /// <summary>"Remove from fleet": out of the EVE Together roster, and only then the separate question whether to
    /// kick the pilot in-game as well. Both steps live in <see cref="FleetMemberRemovalService"/> so this window and
    /// fleet metrics remove a member in exactly the same way — including the case where the in-game kick fails after
    /// the FC agreed to it, which leaves the pilot off the roster and still in the live fleet.</summary>
    private async Task RemoveMemberAsync(FleetMemberInfo member)
    {
        if (!_isOwner || _services.GetService<FleetMemberRemovalService>() is not { } removal)
            return;

        var (status, message) = await removal.RemoveAsync(_fleets, new FleetMemberRemovalRequest(
            _fleet.Id, member.Id, member.CharacterId, NameFor(member.CharacterId), _fleet.Name,
            _esiFleetId, _esiFleetBossId));

        if (status is FleetMemberRemovalStatus.Cancelled)
            return;

        StatusMessage = message;
        if (status is FleetMemberRemovalStatus.RemovedFromFleetInGameFailed)
            _toasts.Show("Removed here, not in-game", message, ToastKind.Warning);

        // No reload here: the removal service announces the removal on the shared watch and this window's own
        // listener redraws on it — the same route by which a removal made in fleet metrics or on the browser card
        // reaches this window. One route in, whoever made the change (ET-52).
    }

    private async Task TransferOwnershipAsync(FleetMemberInfo member)
    {
        if (!_isOwner)
            return;

        if (member.IsExternal)
        {
            StatusMessage = "Ownership can't be transferred to an external pilot (no session).";
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "Transfer ownership", $"Hand ownership of '{_fleet.Name}' to {NameFor(member.CharacterId)}?", okText: "Transfer"))
            return;

        var transferred = await _fleets.TransferFleetOwnershipAsync(_fleet.Id, member.CharacterId);
        StatusMessage = transferred.Ok ? "Ownership transferred." : $"Transfer failed: {transferred.Message}";
        if (transferred.Ok)
            // Two members change hands at once (old owner and new), and so does who may remove whom on every screen.
            await _RosterChangedAsync(FleetRosterChange.Reloaded(_fleet.Id));
    }

    private async Task AddWingAsync()
    {
        if (!_isOwner)
            return;

        var name = await _dialogs.PromptTextAsync("Add wing", "Wing name", "New wing");
        if (name is null)
            return;

        var created = await _fleets.CreateWingAsync(_fleet.Id, name);
        StatusMessage = created.Ok ? $"Wing '{name}' added." : $"Add wing failed: {created.Message}";
        if (created.Ok)
        {
            await ReloadAsync();
            await _AutoApplyStructureAsync();
        }
    }

    private async Task AddSquadAsync(long wingId, string wingName)
    {
        if (!_isOwner)
            return;

        var name = await _dialogs.PromptTextAsync("Add squad", $"Squad name in {wingName}", "New squad");
        if (name is null)
            return;

        var created = await _fleets.CreateSquadAsync(wingId, name);
        StatusMessage = created.Ok ? $"Squad '{name}' added." : $"Add squad failed: {created.Message}";
        if (created.Ok)
        {
            await ReloadAsync();
            await _AutoApplyStructureAsync();
        }
    }

    private async Task DeleteWingAsync(long wingId, string wingName)
    {
        if (!_isOwner)
            return;

        if (!await _dialogs.ConfirmAsync("Delete wing", $"Delete empty wing '{wingName}'?", okText: "Delete"))
            return;

        var deleted = await _fleets.DeleteWingAsync(wingId);
        StatusMessage = deleted.Ok ? $"Wing '{wingName}' deleted." : $"Delete wing failed: {deleted.Message}";
        if (deleted.Ok)
            await ReloadAsync();
    }

    private async Task DeleteSquadAsync(long squadId, string squadName)
    {
        if (!_isOwner)
            return;

        if (!await _dialogs.ConfirmAsync("Delete squad", $"Delete empty squad '{squadName}'?", okText: "Delete"))
            return;

        var deleted = await _fleets.DeleteSquadAsync(squadId);
        StatusMessage = deleted.Ok ? $"Squad '{squadName}' deleted." : $"Delete squad failed: {deleted.Message}";
        if (deleted.Ok)
            await ReloadAsync();
    }

    private async Task RenameWingAsync(long wingId, string currentName)
    {
        if (!_isOwner)
            return;

        var newName = await _dialogs.PromptTextAsync("Rename wing", "Wing name", currentName);
        if (newName is null || string.Equals(newName, currentName, StringComparison.Ordinal))
            return;

        var renamed = await _fleets.RenameWingAsync(wingId, newName);
        StatusMessage = renamed.Ok ? $"Wing renamed to '{newName}'." : $"Rename failed: {renamed.Message}";
        if (!renamed.Ok)
            return;

        // Mirror to the live fleet by the OLD name (the in-game wing still carries it until this push renames it).
        await _MirrorRosterChangeToEsiAsync((control, esiFleetId, bossCharacterId) =>
            control.RenameWingAsync(esiFleetId, bossCharacterId, currentName, newName));
        await ReloadAsync();
    }

    private async Task RenameSquadAsync(long squadId, string wingName, string currentName)
    {
        if (!_isOwner)
            return;

        var newName = await _dialogs.PromptTextAsync("Rename squad", $"Squad name in {wingName}", currentName);
        if (newName is null || string.Equals(newName, currentName, StringComparison.Ordinal))
            return;

        var renamed = await _fleets.RenameSquadAsync(squadId, newName);
        StatusMessage = renamed.Ok ? $"Squad renamed to '{newName}'." : $"Rename failed: {renamed.Message}";
        if (!renamed.Ok)
            return;

        // Mirror to the live fleet by the OLD name, scoped to the squad's wing (squad names aren't unique across wings).
        await _MirrorRosterChangeToEsiAsync((control, esiFleetId, bossCharacterId) =>
            control.RenameSquadAsync(esiFleetId, bossCharacterId, wingName, currentName, newName));
        await ReloadAsync();
    }

    /// <summary>Invites a connected character (role chosen in the dialog) directly onto a wing/squad position — the
    /// invite carries the placement so the member lands there on accept.</summary>
    private async Task InviteToPositionAsync(long wingId, long squadId)
    {
        if (!_isOwner)
            return;

        var memberIds = _members.Select(m => m.CharacterId).ToHashSet();
        var connected = await _fleets.ListConnectedCharactersAsync();
        var options = connected
            .Where(c => c.CharacterId != _actingCharacterId && !memberIds.Contains(c.CharacterId))
            .Select(c => new CharacterPickOption(c.CharacterId, c.CharacterName, "connected", Enabled: true))
            .ToList();
        if (options.Count == 0)
        {
            StatusMessage = "No other connected characters are available to invite.";
            return;
        }

        var invite = await _dialogs.PickFleetInviteAsync(_fleet.Name, options);
        if (invite is null)
            return;

        var invited = await _fleets.CreateInviteAsync(
            _fleet.Id, invite.InviteeCharacterId, invite.Role,
            NoneIfUnset(wingId), NoneIfUnset(squadId), invite.Message);
        StatusMessage = invited.Ok ? "Invite sent." : $"Invite failed: {invited.Message}";
        if (invited.Ok)
        {
            // On a coupled fleet the internal invite alone doesn't reach EVE — also send the in-game ESI invite to the
            // chosen position. This is an explicit invite, so force it regardless of the Auto Invite toggle.
            await _SyncMemberPositionToEsiAsync(invite.InviteeCharacterId, invite.Role, wingId, squadId, true);
            await ReloadAsync();
        }
    }

    /// <summary>Assigns an already-accepted member onto a position by moving them there ("assign accepted
    /// member to position"). The role defaults to the position's level; finer placement is the move-cascade.</summary>
    private async Task AssignAcceptedToPositionAsync(FleetRole role, long wingId, long squadId)
    {
        if (!_isOwner)
            return;

        var options = _members
            .Where(m => !m.IsExternal)
            .Select(m => new CharacterPickOption(
                m.CharacterId, NameFor(m.CharacterId), RoleLabel(m.Role), Enabled: true))
            .ToList();
        if (options.Count == 0)
        {
            StatusMessage = "No accepted members to assign.";
            return;
        }

        var chosen = await _dialogs.PickCharacterAsync("Assign which member to this position?", options);
        if (chosen is null)
            return;

        var member = _members.FirstOrDefault(m => m.CharacterId == chosen.Value);
        if (member is null)
            return;

        await MoveMemberToAsync(member, role, wingId, squadId);
    }

    private static long NoneIfUnset(long id) => id < 0 ? 0 : id;

    private static string RoleLabel(FleetRole role) => role switch
    {
        FleetRole.FleetCommander => "FC",
        FleetRole.WingCommander => "WC",
        FleetRole.SquadCommander => "SC",
        _ => "Member"
    };

    // --- Header / left-panel actions (not node-specific). ---

    [RelayCommand]
    private async Task AddExternal()
    {
        if (!_isOwner)
            return;

        var characterId = await _dialogs.AddExternalMemberAsync(_lookup);
        if (characterId is null)
            return;

        var added = await _fleets.AddExternalMemberAsync(_fleet.Id, characterId.Value);
        StatusMessage = added.Ok ? "External member added." : $"Add failed: {added.Message}";
        if (added.Ok)
            await _RosterChangedAsync(FleetRosterChange.Added(_fleet.Id, characterId.Value));
    }

    /// <summary>General invite (no position) from the panel header; positional invites run from the tree menu.</summary>
    [RelayCommand]
    private Task Invite() => InviteToPositionAsync(-1, -1);

    [RelayCommand]
    private Task AcceptRequest(RosterEntryViewModel? entry) => RespondToRequestAsync(entry, accept: true);

    [RelayCommand]
    private Task DeclineRequest(RosterEntryViewModel? entry) => RespondToRequestAsync(entry, accept: false);

    private async Task RespondToRequestAsync(RosterEntryViewModel? entry, bool accept)
    {
        if (!_isOwner || entry?.JoinRequestId is not { } requestId)
            return;

        var responded = await _fleets.RespondToJoinRequestAsync(requestId, accept);
        StatusMessage = responded.Ok
            ? accept ? "Join request accepted." : "Join request declined."
            : $"Response failed: {responded.Message}";
        if (!responded.Ok)
            return;

        // Only an accept changes the roster; a decline just clears a pending request, which lives on this window
        // alone. A request row carries its request id, not the requester's character id, so this names no pilot.
        if (accept)
            await _RosterChangedAsync(FleetRosterChange.Reloaded(_fleet.Id));
        else
            await ReloadAsync();
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart)
            return;

        var members = await _fleets.ListMembersAsync(_fleet.Id);

        // Refresh the cached roster + the visible FILL from the same fresh fetch the minima check runs on, so the
        // under-strength warning can never contradict the FILL pills the FC is looking at. The displayed fill was
        // built from the cached roster (_members), while this check used a separate fresh fetch — a stale cache made
        // the two disagree (FILL shows all minimums met, yet Start warned under-strength).
        _members = members;
        BuildCompositionFill();

        // B-5: under-strength warns, it never blocks — an FC starts a pug or a roam short on purpose, so this is a
        // question and not a refusal. It stands in front of the start dialog rather than inside it: ET-168 folded it
        // in as a note there, and on a twenty-pilot roster that note sat 208 px below the fold of a scrolling dialog
        // whose START button is pinned to the footer — a warning you press past without it ever coming into view.
        // A question you have to answer cannot scroll away.
        if (!CompositionFillBuilder.AllMinimaMet(_coupledComposition, members)
            && !await _dialogs.ConfirmAsync("Start under-strength?",
                "The coupled doctrine's minimums are not all met yet. Start the fleet anyway?", okText: "Start anyway"))
            return;

        var choice = await _dialogs.PickFleetStartAsync(await BuildStartPromptAsync(members));
        if (choice == FleetStartChoice.Cancel)
            return;

        var started = await _fleets.StartFleetAsync(_fleet.Id);
        if (!started.Ok)
        {
            StatusMessage = $"Start failed: {started.Message}";
            return;
        }

        UpdateActivationLabel(FleetActivation.Active);
        StatusMessage = "Fleet started — now Active.";

        // The ask goes out after the start, never with it: asking someone to leave a running fleet for one that is
        // not running yet is asking them to count for nothing.
        if (choice == FleetStartChoice.AskThemAll)
        {
            var asked = await _fleets.RequestFleetSwitchAsync(_fleet.Id);
            StatusMessage = asked.Ok
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Fleet started — now Active. Asked {asked.Asked} member(s) to switch over.")
                : $"Fleet started, but nobody was asked to switch: {asked.Message}";
        }

        if (_onActivationChanged is not null)
            await _onActivationChanged();
    }

    /// <summary>
    /// What the start dialog is shown from this window (ET-168, scherm 2). Unlike the overview this screen sees one
    /// fleet, so the collision comes wholly from the store the fleet lives in — which is also the only place that
    /// can answer for someone else's pilot. Whose pilot is whose comes from this client's own character register,
    /// because "your own alt you move yourself" turns on exactly that.
    /// </summary>
    private async Task<FleetStartPrompt> BuildStartPromptAsync(IReadOnlyList<FleetMemberInfo> members)
    {
        var elsewhere = (await _fleets.ListMembersActiveElsewhereAsync(_fleet.Id))
            .ToDictionary(m => m.CharacterId, m => m.ElsewhereFleetName);

        var mine = (await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync())
            .Select(c => c.EsiCharacterId)
            .OfType<int>()
            .ToHashSet();

        var rows = members
            .Select(m => new FleetStartMember(
                m.CharacterId,
                NameFor(m.CharacterId),
                mine.Contains(m.CharacterId),
                m.CharacterId == _fleet.CreatorCharacterId,
                m.IsExternal,
                m.IsExternal ? null : elsewhere.GetValueOrDefault(m.CharacterId),
                m.Availability == FleetMemberAvailability.SignedOff))
            .ToList();

        // A client-only fleet's roster is the owner's own pilots and externals: nobody there has an inbox to ask.
        // Read off the transport rather than the fleet, because "client-only" is a property of where it lives.
        return new FleetStartPrompt(_fleet.Name, rows, CanAskThemAll: _fleets is not LocalFleetClient);
    }

    /// <summary>
    /// The way out of an active fleet (ET-166). One button, because the FC's question is "how do I get out of this
    /// tonight" and the three answers to it are only distinguishable side by side: stopping puts the fleet back on
    /// standby with its roster (the recurring op's whole point), concluding is the terminal exit that already
    /// existed, and leaving pulls one of my own pilots out without touching the fleet. Disbanding is deliberately
    /// not among them — it lives on the fleet overview, and standing next to Stop is what made it read as the same
    /// weight of decision.
    /// </summary>
    [RelayCommand]
    private async Task Stop()
    {
        if (!CanStop)
            return;

        switch (await _dialogs.PickFleetExitAsync(await BuildStopPromptAsync()))
        {
            case StopFleetChoice.Stop:
                await StopFleetAsync();
                break;
            case StopFleetChoice.Conclude:
                await ConcludeFleetAsync();
                break;
            case StopFleetChoice.LeaveOnly:
                await LeaveFleet();
                break;
        }
    }

    private async Task<StopFleetPrompt> BuildStopPromptAsync()
    {
        // "Mine" is what this screen already means by it: the characters LEAVE would offer, plus the character I am
        // acting as. Anything left over that is not external is somebody else's pilot.
        HashSet<int> mine = [.. _leavableCharacters.Select(c => c.Id), _actingCharacterId];
        var own = _members.Count(m => !m.IsExternal && mine.Contains(m.CharacterId));
        var external = _members.Count(m => m.IsExternal);
        int? completedRuns = await FleetCompletedRuns.CountAsync(
            _services.GetRequiredService<CqrsDispatcher>(), _fleet.Id, _fleet.CreatedAt.UtcDateTime);

        return new StopFleetPrompt(
            _fleet.Name,
            _fleet.ActivatedAt,
            own,
            _members.Count - own - external,
            external,
            DescribeRunsInProgress(),
            _leavableCharacters.Count,
            completedRuns);
    }

    /// <summary>
    /// This client's own runs still going in this fleet, as lines the dialog prints unchanged. Only this machine's
    /// runs are visible here, and that is the right scope: the FC is being told what stopping does to the
    /// measurements in front of them. Empty when nothing is running, or when the coordinator is not in the graph.
    /// </summary>
    private IReadOnlyList<string> DescribeRunsInProgress() =>
        FleetRunsInProgress.Describe(_services.GetService<FleetRunGroupCodeCoordinator>(), _fleet.Id, NameFor, DateTime.UtcNow);

    private async Task StopFleetAsync()
    {
        var stopped = await _fleets.StopFleetAsync(_fleet.Id);
        if (stopped.Ok)
        {
            UpdateActivationLabel(FleetActivation.Forming);
            StatusMessage = "Fleet stopped — standing by again.";
            _toasts.Show($"Stopped '{_fleet.Name}'", "It is standing by with its roster — press START to run it again.");
            if (_onActivationChanged is not null)
                await _onActivationChanged();
        }
        else
        {
            StatusMessage = $"Stop failed: {stopped.Message}";
            _toasts.Show("Stop failed",
                string.IsNullOrWhiteSpace(stopped.Message) ? $"Could not stop '{_fleet.Name}'." : stopped.Message,
                ToastKind.Error);
        }
    }

    /// <summary>
    /// Conclude, reached from the stop dialog rather than from a header button of its own since ET-166. It carries
    /// no confirmation prompt: the dialog already offered it as the exit marked "cannot be undone" and had the FC
    /// press a red CONCLUDE FLEET, and a second window asking the same question says it worse than the first did.
    /// </summary>
    private async Task ConcludeFleetAsync()
    {
        var concluded = await _fleets.ConcludeFleetAsync(_fleet.Id);
        if (concluded.Ok)
        {
            UpdateActivationLabel(FleetActivation.Concluded);
            StatusMessage = "Fleet concluded.";
            _toasts.Show($"Concluded '{_fleet.Name}'");
            if (_onActivationChanged is not null)
                await _onActivationChanged();
        }
        else
        {
            StatusMessage = $"Conclude failed: {concluded.Message}";
            _toasts.Show("Conclude failed",
                string.IsNullOrWhiteSpace(concluded.Message) ? $"Could not conclude '{_fleet.Name}'." : concluded.Message,
                ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenMetrics() =>
        _dialogs.ShowFleetMetrics(new FleetMetricsViewModel(_services, _fleets, _fleet, _actingCharacterId));

    /// <summary>
    /// Back to the fleet overview (ET-171). Like metrics, this screen had no way back to the place it is opened
    /// from — docked you could reach for the tab strip, floating there was nothing to reach for.
    ///
    /// Opening the overview re-selects the one already open rather than stacking a second: the module host de-dupes
    /// on the module id and refreshes the standing one instead.
    /// </summary>
    [RelayCommand]
    private void BackToFleets() => _dialogs.ShowFleets(new FleetsViewModel(_services));
}
