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
        HeaderSummary = Context.FleetMemberCount > 1 ? Context.FleetStatusText : $"{Rows.Count} characters";

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

        // Whoever the fleet stream has heard from without a run here: a fleet mate on their own client, with the
        // figures they share — or an own character in the fleet that is not on this run.
        foreach (ActivityFleetMemberViewModel member in Context.FleetMembers.Where(member => rows.All(row => row.CharacterId != member.CharacterId)))
        {
            RunShareUpdate? share = Context.GroupCode is { } groupCode ? shares?.Of(groupCode, member.CharacterId) : null;
            FleetCharacterRowViewModel row = _RowFor(member.CharacterId);
            row.Name = member.Name;
            row.IsLocal = _own.Contains(member.CharacterId);
            row.SubText = member.LocationText;
            _ShowFigures(row, FleetCharacterRowViewModel.FiguresOf(member.BountyIsk, member.LootIsk,
                isBountyWithheld: share is { SharesBounty: false }, isLootWithheld: share is { SharesLoot: false }));
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
