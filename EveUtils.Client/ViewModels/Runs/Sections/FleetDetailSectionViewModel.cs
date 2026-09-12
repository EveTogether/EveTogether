using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// FLEET on the detail screen (ET-272): the same one row per character as the run window — what each character behind
/// the activity made, added up by the ISK registry — and under it TOTAL ISK itself, which those rows are the sum of.
/// Taking a Local character out of the loot split is one click on its row, stored at once and added up again.
/// </summary>
public sealed partial class FleetDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Fleet, "FLEET")
{
    private RunDetailSectionInput? _input;

    public ObservableCollection<FleetCharacterRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _totalText = string.Empty;

    /// <summary>One line, only when something needs saying: somebody is out of the loot split, or the names below
    /// could not all be read from the runs themselves (ET-212).</summary>
    [ObservableProperty] private string? _noteText;

    public override bool HasContent => Rows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        _input = input;
        ActivityDetailDto detail = input.Detail;
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>();
        int localRuns = detail.Runs.Count(run => own.Contains(run.CharacterId));

        List<FleetCharacterRowViewModel> rows = [];
        foreach (IGrouping<long, ActivityRunDetailDto> character in detail.Runs.GroupBy(run => run.CharacterId))
        {
            FleetCharacterRowViewModel row = new(character.Key, _ToggleShareAsync)
            {
                Name = input.NameOf(character.Key),
                IsLocal = own.Contains(character.Key),
                Figures = detail.IskByCharacter?.GetValueOrDefault(character.Key) is { } isk
                    ? FleetCharacterRowViewModel.FiguresOf(isk)
                    : [new FleetFigure("nothing", string.Empty, IsQuiet: true)],
                IsSharing = character.All(run => run.IsPayoutEligible),
                // A typed duration is told apart from a measured one only here (ET-98): the corrected moments are
                // written over the start and stop themselves.
                SubText = character.Select(run => run.TimesCorrectedAtUtc).FirstOrDefault(at => at is not null) is { } correctedAtUtc
                    ? $"times corrected by hand at {correctedAtUtc.ToLocalTime():HH:mm}"
                    : null
            };
            row.CanToggleShare = row.IsLocal && localRuns > 1;
            rows.Add(row);
        }

        Rows.Clear();
        foreach (FleetCharacterRowViewModel row in rows
                     .OrderByDescending(row => row.IsLocal)
                     .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(row);

        TotalText = IskFormat.Whole(detail.Isk.Total) + IskFormat.ExpectedPart(detail.Isk);
        FleetCharacterRowViewModel[] local = [.. Rows.Where(row => row.CanToggleShare)];
        decimal? localLoot = local.Length == 0
            ? null
            : local.Sum(row => detail.IskByCharacter?.GetValueOrDefault(row.CharacterId)?.Of(IskSource.Loot)?.Amount ?? 0m);
        NoteText = FleetCharacterRowViewModel.LootSplitText(local, localLoot)
                   // Only once every run here is missing its own recorded name (ET-212) does the caveat still apply.
                   ?? (detail.Runs.Count > 0 && detail.Runs.All(run => !string.IsNullOrEmpty(run.CharacterNameSnapshot))
                       ? null
                       : "Some names are not recorded on their runs, so they are looked up or shown by character id.");
        HeaderSummary = $"{detail.ParticipantCount} character{(detail.ParticipantCount == 1 ? "" : "s")}";
    }

    /// <summary>One click on a Local row: left out of the loot split, or put back, on every run it has in this activity
    /// — and the activity added up again (ET-271's rebuild after SAVE).</summary>
    private async Task _ToggleShareAsync(FleetCharacterRowViewModel row)
    {
        if (_input is not { } input)
            return;

        bool isSharing = !row.IsSharing;
        row.IsSharing = isSharing;
        foreach (ActivityRunDetailDto run in input.Detail.Runs.Where(run => run.CharacterId == row.CharacterId))
        {
            Result written = await services.Dispatcher.Send(new SetRunPayoutEligibilityCommand(run.RunId, isSharing));
            if (!written.IsSuccess)
                return;
        }

        RaiseActivityCorrected();
    }
}
