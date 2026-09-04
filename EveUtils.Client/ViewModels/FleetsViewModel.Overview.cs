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
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.ViewModels.Fleets;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>Which of the three bands the STATUS filter leaves standing.</summary>
public enum FleetStatusFilter
{
    All,
    Active,
    StandingBy,
    Finished,
}

/// <summary>One chip of the CHARACTER filter: a name, and whether it is the one pressed.</summary>
public sealed partial class FleetFilterChipViewModel(string label, int? characterId, IRelayCommand select) : ObservableObject
{
    public string Label { get; } = label;
    public int? CharacterId { get; } = characterId;
    public IRelayCommand SelectCommand { get; } = select;
    [ObservableProperty] private bool _isOn;
}

/// <summary>
/// The overview half of the fleets screen (ET-170): the band with one lane per own character, the three bands
/// ACTIVE · STANDING BY · FINISHED, the link rule that says which started fleet each pilot counts for, and the two
/// layout states the table and the band take from the width they are handed. The loading half — servers, local
/// fleets, member leaves, the per-row actions — is in <c>FleetsViewModel.cs</c>; this file only reads what that half
/// built, once it has, and never talks to a transport of its own.
/// </summary>
public sealed partial class FleetsViewModel
{
    private IReadOnlyList<Character> _knownCharacters = [];
    private readonly HashSet<FleetKey> _openRows = [];
    private readonly List<FleetViewModel> _allRows = [];
    private DispatcherTimer? _clock;
    private double _contentWidth = FleetOverviewLayout.WideBreakpoint;
    private MetricShareSnapshot _sharing = new(new Dictionary<string, string>(StringComparer.Ordinal));

    // ── What the screen binds ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>One lane per character of this client, in the character column's order — never the fleet's members.</summary>
    public ObservableCollection<FleetLaneViewModel> Lanes { get; } = [];

    /// <summary>The compact band's two columns, filled column-first so the eye reads down like the character column.</summary>
    public ObservableCollection<FleetLaneViewModel> CompactLeft { get; } = [];
    public ObservableCollection<FleetLaneViewModel> CompactRight { get; } = [];

    public ObservableCollection<FleetViewModel> ActiveFleets { get; } = [];
    public ObservableCollection<FleetViewModel> StandingByFleets { get; } = [];
    public ObservableCollection<FleetViewModel> FinishedFleets { get; } = [];

    public ObservableCollection<FleetFilterChipViewModel> CharacterChips { get; } = [];

    /// <summary>The character chips the toolbar draws, and the ones it folds behind "⋯". At 758 there is no room for
    /// a chip per pilot — scherm 10 keeps "all N" and the pick that is on, and puts the rest in the overflow — while
    /// the wide toolbar shows every one and folds nothing.</summary>
    public ObservableCollection<FleetFilterChipViewModel> VisibleCharacterChips { get; } = [];

    /// <summary>The folded ones as menu lines, so they use the one fleet menu theme this app already has.</summary>
    public ObservableCollection<FleetMemberMenuItemViewModel> HiddenCharacterChips { get; } = [];

    public bool HasHiddenCharacterChips => HiddenCharacterChips.Count > 0;

    private void SplitCharacterChips()
    {
        VisibleCharacterChips.Clear();
        HiddenCharacterChips.Clear();
        foreach (var chip in CharacterChips)
        {
            if (Layout.IsWide || chip.CharacterId is null || chip.IsOn)
                VisibleCharacterChips.Add(chip);
            else
                HiddenCharacterChips.Add(new(chip.Label, chip.SelectCommand));
        }
        OnPropertyChanged(nameof(HasHiddenCharacterChips));
    }

    /// <summary>The two states of the table and the two densities of the band, from the width the view reports.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWide))]
    [NotifyPropertyChangedFor(nameof(IsCompactBand))]
    [NotifyPropertyChangedFor(nameof(IsLaneBand))]
    [NotifyPropertyChangedFor(nameof(LaneWidth))]
    [NotifyPropertyChangedFor(nameof(LaneMinWidth))]
    [NotifyPropertyChangedFor(nameof(ActionsWidth))]
    [NotifyPropertyChangedFor(nameof(ShowLaneButtons))]
    [NotifyPropertyChangedFor(nameof(LaneIsSlim))]
    private FleetOverviewLayoutState _layout = FleetOverviewLayout.Resolve(FleetOverviewLayout.WideBreakpoint, 0);

    public bool IsWide => Layout.IsWide;
    public bool IsCompactBand => Layout.IsCompactBand;
    public bool IsLaneBand => !Layout.IsCompactBand;
    public double LaneWidth => Layout.LaneWidth;

    /// <summary>What the band's grid is told a lane may not go below. It is the width the resolver already divided
    /// the band into, less a few pixels of slack: the panel counts its own columns, and handing it the exact quotient
    /// would let a pixel of rounding between the two cost a column. Rounding down cannot buy one — the next column up
    /// needs a whole lane more. The view used to carry a literal 236 here, which is why the band stayed on slim cards
    /// after the resolver had already picked full ones.</summary>
    public double LaneMinWidth => Math.Max(1, Layout.LaneWidth - 4);

    /// <summary>What every actions cell in the table is drawn at — header, fleet row, member row and the unfolded
    /// roster's own head, all from this one number so none of them can disagree with the arithmetic that decided
    /// whether JOIN still fits on the row.</summary>
    public double ActionsWidth => Layout.ActionsWidth;
    public bool ShowLaneButtons => Layout.ShowLaneButtons;

    /// <summary>A lane too narrow for its buttons is the mockup's 758 px lane: name, fleet and a smaller clock, no
    /// third line and no foot — the actions live in its context menu.</summary>
    public bool LaneIsSlim => !Layout.ShowLaneButtons;

    [ObservableProperty] private string _headerFleetsText = "0 fleets";
    [ObservableProperty] private string _headerCharactersText = "";
    [ObservableProperty] private string _lanesHeaderText = "";
    [ObservableProperty] private string? _lanesEmptyText;
    [ObservableProperty] private string _activeSummaryText = "0 fleets";
    [ObservableProperty] private string _activeCharactersSummaryText = "";
    [ObservableProperty] private string? _activeEmptyText;
    [ObservableProperty] private string _standingBySummaryText = "0 fleets";
    [ObservableProperty] private string? _standingByEmptyText;
    [ObservableProperty] private string _finishedSummaryText = "0 fleets";
    [ObservableProperty] private string? _finishedEmptyText;
    [ObservableProperty] private string _footerText = "";
    [ObservableProperty] private string _sourcesText = "";

    /// <summary>FINISHED starts folded (ET-170): it is history, and it may not push the fleets that matter tonight
    /// off a 606 px screen.</summary>
    [ObservableProperty] private bool _isFinishedExpanded;

    [RelayCommand]
    private void ToggleFinished() => IsFinishedExpanded = !IsFinishedExpanded;

    /// <summary>The band's right-hand note: that it starts folded, and which filter shows only these.</summary>
    public string FinishedFoldText => IsFinishedExpanded ? "unfolded" : "folded by default";

    partial void OnIsFinishedExpandedChanged(bool value) => OnPropertyChanged(nameof(FinishedFoldText));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusAll))]
    [NotifyPropertyChangedFor(nameof(IsStatusActive))]
    [NotifyPropertyChangedFor(nameof(IsStatusStandingBy))]
    [NotifyPropertyChangedFor(nameof(IsStatusFinished))]
    [NotifyPropertyChangedFor(nameof(ShowActiveGroup))]
    [NotifyPropertyChangedFor(nameof(ShowStandingByGroup))]
    [NotifyPropertyChangedFor(nameof(ShowFinishedGroup))]
    private FleetStatusFilter _statusFilter = FleetStatusFilter.All;

    public bool IsStatusAll => StatusFilter == FleetStatusFilter.All;
    public bool IsStatusActive => StatusFilter == FleetStatusFilter.Active;
    public bool IsStatusStandingBy => StatusFilter == FleetStatusFilter.StandingBy;
    public bool IsStatusFinished => StatusFilter == FleetStatusFilter.Finished;

    public bool ShowActiveGroup => StatusFilter is FleetStatusFilter.All or FleetStatusFilter.Active;
    public bool ShowStandingByGroup => StatusFilter is FleetStatusFilter.All or FleetStatusFilter.StandingBy;
    public bool ShowFinishedGroup => StatusFilter is FleetStatusFilter.All or FleetStatusFilter.Finished;

    [RelayCommand]
    private void SetStatusFilter(FleetStatusFilter filter)
    {
        StatusFilter = filter;
        // Asking for the finished ones is asking to see them.
        if (filter == FleetStatusFilter.Finished)
            IsFinishedExpanded = true;
        ApplyFilters();
    }

    [ObservableProperty] private int? _characterFilter;

    private void SetCharacterFilter(int? characterId)
    {
        CharacterFilter = characterId;
        foreach (var chip in CharacterChips)
            chip.IsOn = chip.CharacterId == characterId;
        SplitCharacterChips();
        ApplyFilters();
    }

    [ObservableProperty] private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>The view reports the width its content root was given; both layout states follow from it.</summary>
    public void ApplyWidth(double contentWidth)
    {
        if (double.IsNaN(contentWidth) || contentWidth <= 0)
            return;
        _contentWidth = contentWidth;
        ApplyLayout();
    }

    // ── The rebuild: from the loaded rows to the screen ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rereads the screen from the rows the loaders built. Runs after each half of the load (servers, local) so a
    /// slow server cannot hold the local fleets back, and on a presence change so a pilot logging in shows up.
    /// </summary>
    private async Task RebuildOverviewAsync()
    {
        await LoadSharingAsync();

        _allRows.Clear();
        _allRows.AddRange(ServerGroups.SelectMany(g => g.Fleets).Concat(LocalFleets));
        var now = DateTimeOffset.UtcNow;

        var links = new ActiveFleetLinks(_allRows
            .Where(r => r.IsInActiveGroup)
            .Select(r => new ActiveFleetRoster(r.Key, r.Info.ActivatedAt, r.Members.Select(m => m.CharacterId).ToList())));

        foreach (var row in _allRows)
        {
            foreach (var member in row.Members)
            {
                member.Presence = FleetMemberPresence.Read(
                    member.IsMine ? _presence?.IsInGame(member.CharacterId, member.CharacterName) : null,
                    PresenceState.Unknown,
                    FleetMemberPresence.IsSilent(member.LastSeenAt, now));
                member.LinkState = LinkStateOf(row, member, links);
                member.ElsewhereNote = member.LinkState == FleetMemberLinkState.ElsewhereActive
                    ? ElsewhereNoteFor(member, links)
                    : null;
                member.SharesNothing = member.IsMine && !row.IsFinished
                    && !_sharing.IsShared(row.Id, member.CharacterId, MetricKind.Dps);
            }

            row.RefreshVisibleMembers();
            DescribeOwnCharacters(row);
            row.IsExpanded = _openRows.Contains(row.Key);
            row.RowExpansionChanged = _RememberRow;
            row.Tick(now);
        }

        BuildLanes(links, now);
        ApplyFilters();
        DescribeTotals(links);
        ApplyLayout();
    }

    /// <summary>The line under an elsewhere-active member: which fleet they count for instead, and that it is the
    /// earlier start that decided it. Both halves matter — the fleet names the situation, the reason says it is a
    /// rule and not a fault.</summary>
    private string? ElsewhereNoteFor(FleetMemberRowViewModel member, ActiveFleetLinks links)
    {
        if (links.LinkedFleetOf(member.CharacterId) is not { } key)
            return null;
        var other = _allRows.FirstOrDefault(r => r.Key == key);
        if (other is null)
            return null;
        return $"{member.CharacterName} is on this roster but counts for {other.Name}, "
             + "because that fleet started first — not in this fleet's metrics, and not in a fleet run here.";
    }

    private static FleetMemberLinkState LinkStateOf(FleetViewModel row, FleetMemberRowViewModel member, ActiveFleetLinks links)
    {
        if (member.IsExternal)
            return FleetMemberLinkState.NoClient;
        if (row.IsFinished)
            return FleetMemberLinkState.Finished;
        if (!row.IsInActiveGroup)
            return FleetMemberLinkState.StandingBy;
        return links.IsLinked(row.Key, member.CharacterId)
            ? FleetMemberLinkState.Linked
            : FleetMemberLinkState.ElsewhereActive;
    }

    /// <summary>"3 linked · 1 elsewhere active" / "2 standing by · Deio, Nilsa" — the "your characters" column, and
    /// the narrow row's second line.</summary>
    private static void DescribeOwnCharacters(FleetViewModel row)
    {
        var mine = row.Members.Where(m => m.IsMine).ToList();
        if (mine.Count == 0 || row.IsFinished)
        {
            row.OwnCharactersText = "—";
            row.OwnCharactersSubText = "";
            return;
        }

        string names = string.Join(", ", mine.Take(3).Select(m => m.CharacterName)) + (mine.Count > 3 ? ", …" : "");
        if (row.IsInActiveGroup)
        {
            int linked = mine.Count(m => m.LinkState == FleetMemberLinkState.Linked);
            int elsewhere = mine.Count(m => m.IsElsewhereActive);
            row.OwnCharactersText = Count(linked, "linked");
            row.OwnCharactersSubText = elsewhere > 0 ? Count(elsewhere, "elsewhere active") : names;
            return;
        }

        row.OwnCharactersText = Count(mine.Count, "standing by");
        row.OwnCharactersSubText = names;
    }

    private void _RememberRow(FleetKey fleet, bool expanded)
    {
        if (expanded)
            _openRows.Add(fleet);
        else
            _openRows.Remove(fleet);
    }

    // ── The band ────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildLanes(ActiveFleetLinks links, DateTimeOffset now)
    {
        Lanes.Clear();
        foreach (var character in _knownCharacters)
        {
            if (character.EsiCharacterId is not { } id)
                continue;

            var fleet = links.LinkedFleetOf(id) is { } key
                ? _allRows.FirstOrDefault(r => r.IsInActiveGroup && r.Key == key)
                : null;
            var seat = fleet?.Members.FirstOrDefault(m => m.CharacterId == id);
            bool isCommander = seat?.IsFleetCommander ?? false;
            var elsewhere = links.ActiveFleetsOf(id).Where(k => fleet is null || k != fleet.Key)
                .Select(k => _allRows.FirstOrDefault(r => r.Key == k))
                .OfType<FleetViewModel>()
                .ToList();
            var standingBy = _allRows.Where(r => r.IsStandingBy && r.Members.Any(m => m.CharacterId == id)).ToList();
            bool? inGame = _presence?.IsInGame(id, character.Name);

            var lane = new FleetLaneViewModel(character)
            {
                Fleet = fleet,
                IsFleetCommander = isCommander,
                IsElsewhereActive = elsewhere.Count > 0,
                StandingByCount = standingBy.Count,
                IsInGame = inGame,
                WhereText = fleet is null
                    ? inGame switch { true => "online", false => "offline", null => "" }
                    : isCommander
                        ? Count(fleet.Members.Count(m => m.LinkState == FleetMemberLinkState.Linked), "linked")
                          + (fleet.Members.Any(m => m.IsExternal) ? " · " + Count(fleet.Members.Count(m => m.IsExternal), "external") : "")
                        : $"FC: {fleet.CommanderText}{(fleet.IsMine ? " (you)" : "")}",
                PrimaryActionText = fleet is null ? "START…" : fleet.IsMine && isCommander ? "STOP" : "LEAVE",
                PrimaryIsAccent = fleet is null,
                PrimaryCommand = fleet is null
                    ? StartForCharacterCommandFor(id, standingBy)
                    : fleet.IsMine && isCommander
                        ? new AsyncRelayCommand(() => StopRowAsync(fleet))
                        : new AsyncRelayCommand(() => LeaveCharacterAsync(fleet, id)),
                SecondaryActionText = fleet is null ? "" : isCommander ? "manage" : "metrics",
                SecondaryCommand = fleet is null ? null
                    : isCommander ? new RelayCommand(() => ManageRow(fleet)) : new AsyncRelayCommand(() => MetricsRowAsync(fleet)),
                RevealCommand = fleet is null ? null : new RelayCommand(() => Reveal(fleet)),
                MenuItems = LaneMenu(fleet, id, isCommander, standingBy),
            };

            if (fleet is not null)
            {
                if (fleet.Info.ActivatedAt is { } started)
                    lane.FootChips.Add(new(string.Create(CultureInfo.InvariantCulture, $"active since {started.ToLocalTime():HH:mm}"), FleetChipTone.Ok));
                if (seat?.SharesNothing == true)
                    lane.FootChips.Add(new("shares nothing", FleetChipTone.Warn));
                foreach (var other in elsewhere)
                    lane.FootChips.Add(new($"also on {other.Name} — not linked there", FleetChipTone.Warn));
            }
            if (standingBy.Count > 0)
                lane.FootChips.Add(new(standingBy.Count == 1 ? "standing by in 1 fleet" : Count(standingBy.Count, "standing by in") + " fleets"));

            lane.Tick(now);
            Lanes.Add(lane);
        }

        LanesEmptyText = Lanes.Count == 0 ? "No characters yet — add one in the CHARACTERS column." : null;
    }

    /// <summary>START… on an idle lane: the one standing-by fleet this pilot commands starts outright; with several
    /// the row's own START is the place to choose, and with none the button says why it does nothing.</summary>
    private IRelayCommand StartForCharacterCommandFor(int characterId, IReadOnlyList<FleetViewModel> standingBy)
    {
        var owned = standingBy.Where(r => r.IsMine).ToList();
        return owned.Count == 1
            ? new AsyncRelayCommand(() => StartRowAsync(owned[0]))
            : new RelayCommand(() =>
            {
                if (owned.Count > 1)
                {
                    StatusMessage = $"{NameOf(characterId)} commands {owned.Count} fleets standing by — press START on the one to run.";
                    SetStatusFilter(FleetStatusFilter.StandingBy);
                }
                else if (standingBy.Count > 0)
                    StatusMessage = $"{NameOf(characterId)} stands by in {standingBy.Count} fleet{(standingBy.Count == 1 ? "" : "s")}, but only the fleet commander starts one.";
                else
                    StatusMessage = $"{NameOf(characterId)} is in no fleet — create one with + NEW FLEET or join one below.";
            });
    }

    private IReadOnlyList<FleetMemberMenuItemViewModel> LaneMenu(
        FleetViewModel? fleet, int characterId, bool isCommander, IReadOnlyList<FleetViewModel> standingBy)
    {
        var items = new List<FleetMemberMenuItemViewModel>();
        if (fleet is null)
        {
            foreach (var row in standingBy)
                items.Add(new($"START {row.Name}", row.IsMine ? new AsyncRelayCommand(() => StartRowAsync(row)) : null,
                    row.IsMine ? null : "Only the fleet commander starts a fleet"));
            if (items.Count == 0)
                items.Add(new("no fleet standing by for this character"));
            return items;
        }

        if (fleet.IsMine && isCommander)
            items.Add(new("STOP the fleet", new AsyncRelayCommand(() => StopRowAsync(fleet))));
        else
            items.Add(new($"LEAVE {fleet.Name}", new AsyncRelayCommand(() => LeaveCharacterAsync(fleet, characterId))));
        items.Add(new(fleet.RosterButtonLabel.ToLowerInvariant(), new RelayCommand(() => ManageRow(fleet))));
        items.Add(new("metrics", new AsyncRelayCommand(() => MetricsRowAsync(fleet))));
        items.Add(new("sharing", new AsyncRelayCommand(() => OpenSharing(fleet))));
        return items;
    }

    private string NameOf(int characterId) =>
        _knownCharacters.FirstOrDefault(c => c.EsiCharacterId == characterId)?.Name ?? $"Char {characterId}";

    // ── The bands and their filters ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The order inside a group (scherm 1 and 10 draw the same one): the fleets you own first — those are the ones
    /// you can start, stop and manage, so those are the ones you came for — then the ones you only fly in. Within
    /// each, most recently run first, and a fleet that has never run falls back on when it was made. Left to itself
    /// the list came out in load order, servers before local, which put your own running fleet under someone else's.
    /// </summary>
    private static IEnumerable<FleetViewModel> InScreenOrder(IEnumerable<FleetViewModel> rows) =>
        rows.OrderByDescending(r => r.IsMine)
            .ThenByDescending(r => r.Info.ActivatedAt ?? r.Info.CreatedAt)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    private void ApplyFilters()
    {
        IEnumerable<FleetViewModel> rows = _allRows;
        if (CharacterFilter is { } id)
            rows = rows.Where(r => r.Members.Any(m => m.CharacterId == id));
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            rows = rows.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.CommanderText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Members.Any(m => m.CharacterName.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        var kept = rows.ToList();
        Refill(ActiveFleets, InScreenOrder(kept.Where(r => r.IsInActiveGroup)));
        Refill(StandingByFleets, InScreenOrder(kept.Where(r => r.IsStandingBy)));
        Refill(FinishedFleets, InScreenOrder(kept.Where(r => r.IsFinished)));

        bool filtered = CharacterFilter is not null || !string.IsNullOrWhiteSpace(SearchText);
        ActiveEmptyText = ActiveFleets.Count > 0 ? null : filtered ? "No started fleet matches the filter." : "No fleet is running.";
        StandingByEmptyText = StandingByFleets.Count > 0 ? null : filtered ? "Nothing standing by matches the filter." : "Nothing is standing by — + NEW FLEET puts one here.";
        FinishedEmptyText = FinishedFleets.Count > 0 ? null : "No finished fleets.";
    }

    private static void Refill(ObservableCollection<FleetViewModel> target, IEnumerable<FleetViewModel> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    private void DescribeTotals(ActiveFleetLinks links)
    {
        int total = _allRows.Count;
        int active = _allRows.Count(r => r.IsInActiveGroup);
        int standingBy = _allRows.Count(r => r.IsStandingBy);
        int finished = _allRows.Count(r => r.IsFinished);
        int pilots = Lanes.Count;
        int inFleet = Lanes.Count(l => !l.IsIdle);

        HeaderFleetsText = $"{Count(total, "fleet", "fleets")} · {active.ToString(CultureInfo.InvariantCulture)} active";
        HeaderCharactersText = pilots == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{inFleet} of your {pilots} characters in an active fleet");
        LanesHeaderText = pilots == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{inFleet} of {pilots} characters");
        ActiveSummaryText = Count(active, "fleet", "fleets");
        ActiveCharactersSummaryText = pilots == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{inFleet} of your {pilots} characters");
        StandingBySummaryText = Count(standingBy, "fleet", "fleets");
        FinishedSummaryText = Count(finished, "fleet", "fleets");
        FooterText = $"{Count(total, "fleet", "fleets")} · {active.ToString(CultureInfo.InvariantCulture)} active · {standingBy.ToString(CultureInfo.InvariantCulture)} standing by · {finished.ToString(CultureInfo.InvariantCulture)} finished";

        int servers = ServerGroups.Count(g => g.Fleets.Count > 0);
        SourcesText = (LocalFleets.Count > 0, servers) switch
        {
            (true, 0) => "local",
            (true, var n) => $"local + {Count(n, "server", "servers")}",
            (false, 0) => "",
            (false, var n) => Count(n, "server", "servers"),
        };

        CharacterChips.Clear();
        if (pilots > 0)
        {
            CharacterChips.Add(new(string.Create(CultureInfo.InvariantCulture, $"all {pilots}"), null, new RelayCommand(() => SetCharacterFilter(null))) { IsOn = CharacterFilter is null });
            foreach (var character in _knownCharacters)
            {
                if (character.EsiCharacterId is not { } id)
                    continue;
                CharacterChips.Add(new(character.Name, id, new RelayCommand(() => SetCharacterFilter(id))) { IsOn = CharacterFilter == id });
            }
        }

        SplitCharacterChips();
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}");
    private static string Count(int n, string one, string many) => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");

    // ── Layout ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void ApplyLayout()
    {
        Layout = FleetOverviewLayout.Resolve(_contentWidth, Lanes.Count);   // padding follows the state the width implies

        foreach (var row in _allRows)
        {
            row.IsWide = Layout.IsWide;
            row.ActionsWidth = Layout.ActionsWidth;
            BuildOverflow(row);
        }

        foreach (var lane in Lanes)
            lane.IsSlim = !Layout.ShowLaneButtons;

        SplitCharacterChips();

        // Column-first: the first half of the roster down the left column, the rest down the right.
        int left = (Lanes.Count + 1) / 2;
        CompactLeft.Clear();
        CompactRight.Clear();
        for (int i = 0; i < Lanes.Count; i++)
            (i < left ? CompactLeft : CompactRight).Add(Lanes[i]);
    }

    /// <summary>
    /// What goes behind the "⋯" on a row, and — the one thing that is decided rather than merely sorted — whether
    /// JOIN / REQUEST gets a place on the row itself. Every action this row allows is drawn exactly once: on the row
    /// when its width and its group give it a place there, in this menu otherwise; never in both, never nowhere. The
    /// order things fold in is scherm 15's: STOP/START and MANAGE/VIEW keep the row at any width, SHARE folds first,
    /// then METRICS, then LEAVE. The rarer management actions — edit, disband, adding pilots to a local fleet — live
    /// here whatever the width.
    ///
    /// JOIN belongs on the row when it fits (Jithran, 2026-09-04) and nothing scherm 1 draws may step aside for it,
    /// so it is measured rather than ruled: what the standing buttons take, plus JOIN, plus the "⋯" if anything is
    /// left to fold, against the width the actions cell actually has. It never stands on a narrow row — there the
    /// row is two buttons and an overflow, which is what scherm 10 and scherm 15 both give it.
    /// </summary>
    private void BuildOverflow(FleetViewModel row)
    {
        // Everything that folds no matter what. Built first, because whether the "⋯" itself stands is part of the
        // width JOIN has to fit beside.
        var folded = new List<FleetMemberMenuItemViewModel>();
        bool inFleet = row.IsMine || row.IsParticipating;
        if (inFleet && !row.IsFinished && !row.ShowMetricsButton)
            folded.Add(new("METRICS", new AsyncRelayCommand(() => MetricsRowAsync(row))));
        if (row.IsMine && !row.IsFinished && !row.ShowShareButton)
            folded.Add(new("SHARE", new AsyncRelayCommand(() => OpenSharing(row))));
        if (row.CanLeave && !row.ShowLeave)
            folded.Add(new("LEAVE", new AsyncRelayCommand(() => LeaveRowAsync(row))));
        if (row.IsLocal && row.IsMine && !row.IsFinished)
        {
            folded.Add(new("ADD CHARACTER", new AsyncRelayCommand(() => AddLocalCharacter(row))));
            folded.Add(new("ADD EXTERNAL PILOT", new AsyncRelayCommand(() => AddLocalExternal(row))));
        }
        if (row.ShowOwnerActions && !row.IsFinished)
            folded.Add(new("EDIT", new AsyncRelayCommand(() => EditFleet(row))));
        if (row.IsMine && !row.IsFinished)
            folded.Add(new("DISBAND", new AsyncRelayCommand(() => DeleteRowAsync(row)), "Archives the fleet. Not the same as STOP."));

        bool wantsJoin = row.CanJoin || row.CanRequest;
        double onTheRow = row.StandingActionsWidth + row.JoinActionWidth
                        + (folded.Count > 0 ? FleetRowActionWidths.Overflow : 0);
        row.JoinOnRow = wantsJoin && row.IsWide && onTheRow <= row.ActionsWidth;

        row.OverflowItems.Clear();
        foreach (var item in folded)
            row.OverflowItems.Add(item);
        if (row.CanJoin && !row.ShowJoin)
            row.OverflowItems.Add(new("JOIN WITH ANOTHER CHARACTER", row.JoinEnabled ? new AsyncRelayCommand(() => Join(row)) : null, row.JoinHint));
        if (row.CanRequest && !row.ShowRequest)
            row.OverflowItems.Add(new("REQUEST FOR ANOTHER CHARACTER", row.JoinEnabled ? new AsyncRelayCommand(() => Request(row)) : null, row.JoinHint));
        row.OverflowChanged();
    }

    // ── The clock ───────────────────────────────────────────────────────────────────────────────────────────────

    private void StartClock(bool run)
    {
        if (!run)
            return;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += _OnClockTick;
        _clock.Start();
    }

    private void StopClock()
    {
        if (_clock is null)
            return;
        _clock.Stop();
        _clock.Tick -= _OnClockTick;
        _clock = null;
    }

    private void _OnClockTick(object? sender, EventArgs e) => Tick(DateTimeOffset.UtcNow);

    /// <summary>Advances every clock on the screen to <paramref name="now"/>. Public so a test owns the time.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var lane in Lanes)
            lane.Tick(now);
        foreach (var row in _allRows.Where(r => r.IsInActiveGroup))
            row.Tick(now);
    }

    // ── Row actions the overview adds (ET-170): the lifecycle verbs that used to live in the roster only ──────

    /// <summary>Unfold this fleet's row in the table — a lane in the band points at the same fleet below it.</summary>
    private void Reveal(FleetViewModel row)
    {
        if (!row.IsExpanded)
            row.ToggleExpandedCommand.Execute(null);
    }

    [RelayCommand]
    private void ManageRow(FleetViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsLocal)
            ManageLocal(row);
        else
            Manage(row);
    }

    [RelayCommand]
    private Task MetricsRowAsync(FleetViewModel? row) =>
        row is null ? Task.CompletedTask : row.IsLocal ? OpenMetricsLocal(row) : OpenMetrics(row);

    [RelayCommand]
    private Task ShareRowAsync(FleetViewModel? row) => row is null ? Task.CompletedTask : OpenSharing(row);

    /// <summary>DISBAND on a finished row, and behind "⋯" elsewhere — the same archive as before, routed by origin.</summary>
    [RelayCommand]
    private Task DeleteRowAsync(FleetViewModel? row) =>
        row is null ? Task.CompletedTask : row.IsLocal ? DisbandLocal(row) : Disband(row);

    /// <summary>
    /// START on a standing-by row: the same gate the roster window's START runs — warn when the coupled doctrine's
    /// minima are not met, offer the ESI-invite prompt for externals — and then the fleet is Active. Owner-only, as
    /// the server enforces; the button is disabled for anyone else rather than hidden.
    /// </summary>
    [RelayCommand]
    private async Task StartRowAsync(FleetViewModel? row)
    {
        if (row is null || !row.StartEnabled)
            return;

        var client = ServerOrLocalClient(row.ServerAddress, row.ActingCharacterId);
        var members = await client.ListMembersAsync(row.Id);
        var composition = row.Info.FleetCompositionId is { } compositionId
            ? await CompositionClientFor(row.ServerAddress, row.ActingCharacterId).GetAsync(compositionId)
            : null;

        if (!CompositionFillBuilder.AllMinimaMet(composition, members)
            && !await _dialogs.ConfirmAsync("Start under-strength?",
                "The coupled doctrine's minimums are not all met yet. Start the fleet anyway?", okText: "Start anyway"))
            return;

        if (!await _dialogs.ConfirmStartFleetAsync(row.Name, members.Count(m => m.IsExternal)))
            return;

        var started = await client.StartFleetAsync(row.Id);
        if (started.Ok)
        {
            StatusMessage = $"Started '{row.Name}'.";
            _toasts.Show($"Started '{row.Name}'", "Its members are linked from now on.");
            await _ReloadEverythingAsync();
        }
        else
        {
            StatusMessage = $"Start failed: {started.Message}";
            _toasts.Show("Start failed",
                string.IsNullOrWhiteSpace(started.Message) ? $"Could not start '{row.Name}'." : started.Message, ToastKind.Error);
        }
    }

    /// <summary>
    /// STOP on a started row opens the same three-way exit the roster window opens (ET-166): stop back to standing
    /// by, conclude for good, or pull one of my own pilots out. The dialog names this client's runs still going so
    /// the FC knows stopping does not throw them away.
    /// </summary>
    [RelayCommand]
    private async Task StopRowAsync(FleetViewModel? row)
    {
        if (row is null || !row.StopEnabled)
            return;

        var mine = row.Members.Where(m => m.IsMine).ToList();
        var leavable = mine.Where(m => m.CharacterId != row.Info.CreatorCharacterId).ToList();
        int external = row.Members.Count(m => m.IsExternal);
        var prompt = new StopFleetPrompt(
            row.Name,
            row.Info.ActivatedAt,
            mine.Count,
            row.Members.Count - mine.Count - external,
            external,
            FleetRunsInProgress.Describe(_services.GetService<FleetRunGroupCodeCoordinator>(), row.Id, NameOf, DateTime.UtcNow),
            leavable.Count);

        var client = ServerOrLocalClient(row.ServerAddress, row.ActingCharacterId);
        switch (await _dialogs.PickFleetExitAsync(prompt))
        {
            case StopFleetChoice.Stop:
                var stopped = await client.StopFleetAsync(row.Id);
                if (stopped.Ok)
                {
                    StatusMessage = $"Stopped '{row.Name}' — standing by again.";
                    _toasts.Show($"Stopped '{row.Name}'", "It is standing by with its roster — press START to run it again.");
                }
                else
                {
                    StatusMessage = $"Stop failed: {stopped.Message}";
                    _toasts.Show("Stop failed", string.IsNullOrWhiteSpace(stopped.Message) ? $"Could not stop '{row.Name}'." : stopped.Message, ToastKind.Error);
                    return;
                }
                break;
            case StopFleetChoice.Conclude:
                var concluded = await client.ConcludeFleetAsync(row.Id);
                if (concluded.Ok)
                {
                    StatusMessage = $"Concluded '{row.Name}'.";
                    _toasts.Show($"Concluded '{row.Name}'");
                }
                else
                {
                    StatusMessage = $"Conclude failed: {concluded.Message}";
                    _toasts.Show("Conclude failed", string.IsNullOrWhiteSpace(concluded.Message) ? $"Could not conclude '{row.Name}'." : concluded.Message, ToastKind.Error);
                    return;
                }
                break;
            case StopFleetChoice.LeaveOnly:
                await LeaveCharactersAsync(row, leavable);
                return;   // leaving reloads by itself
            default:
                return;
        }

        await _ReloadEverythingAsync();
    }

    /// <summary>LEAVE on a row I am a member of: one of my characters goes, or the picker asks which.</summary>
    [RelayCommand]
    private Task LeaveRowAsync(FleetViewModel? row) =>
        row is null ? Task.CompletedTask
            : LeaveCharactersAsync(row, row.Members.Where(m => m.IsMine && m.CharacterId != row.Info.CreatorCharacterId).ToList());

    private async Task LeaveCharactersAsync(FleetViewModel row, IReadOnlyList<FleetMemberRowViewModel> candidates)
    {
        if (candidates.Count == 0)
        {
            StatusMessage = $"None of your characters can leave '{row.Name}' — the owner hands the fleet over or disbands it.";
            return;
        }

        IReadOnlyList<int>? chosen = candidates.Count == 1
            ? [candidates[0].CharacterId]
            : await _dialogs.PickCharactersAsync($"Leave '{row.Name}' with which character(s)?",
                candidates.Select(c => new CharacterPickOption(c.CharacterId, c.CharacterName, "member", Enabled: true)).ToList());
        if (chosen is null || chosen.Count == 0)
            return;

        foreach (int characterId in chosen)
            await LeaveCharacterAsync(row, characterId);
    }

    /// <summary>One of my characters out of one fleet. A server fleet is left; a client-only fleet's roster is this
    /// client's own, so the pilot is removed from it through the shared removal flow.</summary>
    private async Task LeaveCharacterAsync(FleetViewModel row, int characterId)
    {
        var member = row.Members.FirstOrDefault(m => m.CharacterId == characterId);
        string name = member?.CharacterName ?? NameOf(characterId);

        if (row.ServerAddress is { } server)
        {
            await LeaveMemberAsync(server, row.Id, characterId, name, row.Name);
            return;
        }

        if (member is null || _services.GetService<FleetMemberRemovalService>() is not { } removal)
            return;

        var (status, message) = await removal.RemoveAsync(
            ServerOrLocalClient(null, row.ActingCharacterId),
            new FleetMemberRemovalRequest(row.Id, member.MemberId, characterId, name, row.Name,
                row.Info.EsiFleetId, row.Info.EsiFleetBossId));
        if (status is not FleetMemberRemovalStatus.Cancelled)
            StatusMessage = message;
    }

    // ── Sharing, read once per rebuild ──────────────────────────────────────────────────────────────────────────

    /// <summary>Which of my characters share nothing with a fleet — the per-fleet override over the global default,
    /// the same reading the SHARE dialog starts from. A read that fails keeps the last snapshot rather than
    /// flagging every pilot amber.</summary>
    private async Task LoadSharingAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<EveUtils.Shared.Cqrs.IDispatcher>().Query(new GetSettingsQuery());
            _sharing = new MetricShareSnapshot(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
        }
        catch (Exception)
        {
            // Keep what we had.
        }
    }
}
