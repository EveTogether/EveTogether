using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// FLEET in the run window (ET-272): one row per character — what each one made in this run — and the total they add
/// up to, which is TOTAL ISK itself. This client's own characters come first, tagged "Local", with their own runs'
/// figures; a fleet mate follows with what their client shares over the fleet stream, which is theirs and not in the
/// total. Gone rather than empty when no fleet has ever reported in: a FLEET section standing open on a solo run reads
/// as a measurement, and nothing here can measure the absence of a fleet.
/// </summary>
public sealed partial class FleetWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Fleet, "FLEET")
{
    private IReadOnlySet<long> _own = new HashSet<long>();
    private readonly Dictionary<int, string?> _names = [];
    private bool _isOwnLoaded;
    private bool _isLoadingOwn;

    public override bool IsShown => Context.IsFleetShown;

    public ObservableCollection<FleetCharacterRowViewModel> Rows { get; } = [];

    /// <summary>TOTAL ISK, the header's own text — the sum of the Local rows, never added up again here.</summary>
    [ObservableProperty] private string _totalText = string.Empty;

    [ObservableProperty] private bool _isTotalShown;

    /// <summary>The one line under the total, only when something needs saying: somebody is out of the loot split,
    /// or a fleet mate's figures stand beside the total without being in it.</summary>
    [ObservableProperty] private string? _noteText;

    // Never "solo": nothing here can observe the absence of a fleet, only the presence of one. Without any the section
    // is hidden (IsShown) and this line is not on screen at all.
    public override void RefreshSummary() =>
        HeaderSummary = _readiness ?? (Context.FleetMemberCount > 1 ? Context.FleetStatusText : $"{Rows.Count} characters");

    // "2 of 3 ready · 1 offline" over the whole roster (ET-440), the FC's answer to "who am I going in with"; null
    // while no fleet is known, so a solo run keeps its own line.
    private string? _readiness;

    private static string? _ReadinessOf(IReadOnlyList<FleetMateStatus> standings)
    {
        if (standings.Count < 2)
            return null;

        int ready = standings.Count(standing => standing.IsConnected is not false && standing.Reason
            is FleetMemberStatusReason.InSystem or FleetMemberStatusReason.NoSystemYet
            or FleetMemberStatusReason.LocationWithheld or FleetMemberStatusReason.OldClient
            or FleetMemberStatusReason.ReportingElsewhere);
        int offline = standings.Count(standing => standing.Reason
            is FleetMemberStatusReason.NotInGame or FleetMemberStatusReason.Silent or FleetMemberStatusReason.NotConnected);
        return offline > 0
            ? $"{ready} of {standings.Count} ready · {offline} offline"
            : $"{ready} of {standings.Count} ready";
    }

    public override void Refresh(DateTime nowUtc) => _ = _LoadOwnAsync();

    protected override void OnContextChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(IRunWindowContext.IsFleetShown):
                OnPropertyChanged(nameof(IsShown));
                break;
            case nameof(IRunWindowContext.CharacterIsk) or nameof(IRunWindowContext.FleetMembers):
                _Rebuild();
                break;
        }
    }

    /// <summary>A new list only when a figure actually moved (ET-287): the list is built again every clock tick, and
    /// handing the row an equal copy still redrew every figure on every row once a second.</summary>
    private static void _ShowFigures(FleetCharacterRowViewModel row, IReadOnlyList<FleetFigure> figures)
    {
        if (!row.Figures.SequenceEqual(figures))
            row.Figures = figures;
    }

    private void _Rebuild()
    {
        List<FleetCharacterRowViewModel> rows = [];
        FleetRunShares? shares = Context.Services.GetService<FleetRunShares>();
        int localRuns = Context.Participants.Count(participant => _own.Contains(participant.CharacterId));

        foreach (IGrouping<int, RunParticipantViewModel> character in Context.Participants.GroupBy(participant => participant.CharacterId))
        {
            FleetCharacterRowViewModel row = _RowFor(character.Key);
            row.Name = character.First().CharacterName;
            row.IsLocal = _own.Contains(character.Key);
            row.SubText = null;
            _ShowFigures(row, Context.CharacterIsk.TryGetValue(character.Key, out IskBreakdown? isk)
                ? FleetCharacterRowViewModel.FiguresOf(isk)
                : [new FleetFigure("nothing", string.Empty, IsQuiet: true)]);
            row.IsSharing = character.All(participant => participant.IsPayoutEligible);
            row.CanToggleShare = row.IsLocal && localRuns > 1;
            rows.Add(row);
        }

        // Every other member of the fleet (ET-440): whoever the stream has heard from, and the roster's members who
        // sent nothing at all — each with the reason they read the way they do, instead of only those with a system.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<FleetMateStatus> standings = Context.FleetId is { } fleetId
            && Context.Services.GetService<FleetMemberBoard>() is { } board
                ? board.Read(fleetId, now)
                : [];
        _readiness = _ReadinessOf(standings);
        IEnumerable<int> mates = Context.FleetMembers.Select(member => member.CharacterId)
            .Concat(standings.Select(standing => standing.CharacterId))
            .Distinct()
            .Where(characterId => rows.All(row => row.CharacterId != characterId));
        foreach (int characterId in mates)
        {
            ActivityFleetMemberViewModel? member = Context.FleetMembers.FirstOrDefault(entry => entry.CharacterId == characterId);
            FleetMateStatus? standing = standings.FirstOrDefault(entry => entry.CharacterId == characterId);
            RunShareUpdate? share = Context.GroupCode is { } groupCode ? shares?.Of(groupCode, characterId) : null;
            FleetCharacterRowViewModel row = _RowFor(characterId);
            row.Name = member?.Name ?? _NameOf(characterId);
            row.IsLocal = _own.Contains(characterId);
            row.SubText = standing is null
                ? member?.LocationText
                : FleetMemberStatusText.Line(standing, member?.LocationText, now);
            row.StatusChips = standing is null || row.IsLocal
                ? []
                : FleetMemberStatusText.Chips(standing, Context.GroupCode is null ? null : share is not null, now);
            // An own character with no run here made nothing in it, whatever the fleet stream last said about it
            // (ET-309) — this client knows every run of its own, so it takes its own word over the stream's.
            _ShowFigures(row, row.IsLocal
                ? [new FleetFigure("not in this run", string.Empty, IsQuiet: true)]
                : FleetCharacterRowViewModel.FiguresOf(member?.BountyIsk, member?.LootIsk,
                    isBountyWithheld: share is { SharesBounty: false }, isLootWithheld: share is { SharesLoot: false },
                    ore: Context.FleetMateOreIsk(characterId)));
            row.IsSharing = true;
            row.CanToggleShare = false;
            rows.Add(row);
        }

        List<FleetCharacterRowViewModel> ordered = [.. rows
            .OrderByDescending(row => row.IsLocal)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];
        if (!Rows.SequenceEqual(ordered))
        {
            Rows.Clear();
            foreach (FleetCharacterRowViewModel row in ordered)
                Rows.Add(row);
        }

        TotalText = Context.GroupTotalIskText;
        IsTotalShown = Context.HasGroupTotalIsk && Context.Participants.Count > 0;
        NoteText = _Note(ordered);
        RefreshSummary();
    }

    private string? _Note(IReadOnlyList<FleetCharacterRowViewModel> rows)
    {
        FleetCharacterRowViewModel[] local = [.. rows.Where(row => row.CanToggleShare)];
        decimal? localLoot = local.Length == 0
            ? null
            : local.Sum(row => Context.CharacterIsk.GetValueOrDefault(row.CharacterId)?.Of(IskSource.Loot)?.Amount ?? 0m);
        if (FleetCharacterRowViewModel.LootSplitText(local, localLoot) is { } split)
            return split;

        return rows.Any(row => !row.IsLocal && Context.Participants.All(participant => participant.CharacterId != row.CharacterId))
            ? "Fleet mates' figures are their own — not in this total."
            : null;
    }

    /// <summary>A roster member who never sent a sample has no name from the stream: asked once, best-effort, the way
    /// the window names the members it hears from.</summary>
    private string _NameOf(int characterId)
    {
        if (_names.TryGetValue(characterId, out string? known))
            return known ?? $"Char {characterId}";

        _names[characterId] = null;
        _ = _ResolveNameAsync(characterId);
        return $"Char {characterId}";
    }

    private async Task _ResolveNameAsync(int characterId)
    {
        if (Context.Services.GetService<IExternalCharacterLookup>() is not { } lookup)
            return;

        ExternalCharacterInfo info = await lookup.LookupAsync(characterId);
        if (!info.Exists)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _names[characterId] = info.Name;
            _Rebuild();
        });
    }

    private FleetCharacterRowViewModel _RowFor(long characterId) =>
        Rows.FirstOrDefault(row => row.CharacterId == characterId) ?? new FleetCharacterRowViewModel(characterId, _ToggleShareAsync);

    /// <summary>One click on a Local row (ET-272): the character is left out of the loot split, or put back — stored on
    /// every run it has here, and the rest recomputed on the spot.</summary>
    private async Task _ToggleShareAsync(FleetCharacterRowViewModel row)
    {
        bool isSharing = !row.IsSharing;
        RunParticipantViewModel[] runs = [.. Context.Participants.Where(participant => participant.CharacterId == row.CharacterId)];
        foreach (RunParticipantViewModel participant in runs)
            participant.IsPayoutEligible = isSharing;
        _Rebuild();

        if (Context.Services.GetService<CqrsDispatcher>() is null)
            return;
        using IServiceScope scope = Context.Services.CreateScope();
        CqrsDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<CqrsDispatcher>();
        foreach (RunParticipantViewModel participant in runs)
            await dispatcher.Send(new SetRunPayoutEligibilityCommand(participant.RunId, isSharing));
    }

    private async Task _LoadOwnAsync()
    {
        if (_isOwnLoaded || _isLoadingOwn)
            return;

        _isLoadingOwn = true;
        try
        {
            _own = await AttendanceRoster.OwnCharacterIdsAsync(Context.Services);
            _isOwnLoaded = true;
            _Rebuild();
        }
        finally
        {
            _isLoadingOwn = false;
        }
    }
}
