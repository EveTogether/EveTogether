using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>MINING on the detail screen: what the gamelog recorded, per character and per ore, with the total apart
/// (ET-229) — general for any mining, not only a homefront's.
///
/// <see cref="Groups"/> (ET-283) is the same run-window shape, one row per character: every character behind this
/// activity already has its own persisted <c>RunMiningEntry</c> rows once its run synced into the group — own
/// characters and a fleet mate's alike, the same way FLEET's own detail screen groups <c>detail.Runs</c>
/// (<see cref="FleetDetailSectionViewModel"/>) without needing the run window's live share wire at all. There is no
/// "not shared" row here, for the same reason FLEET's detail has none: an external character with no Eve Together
/// never leaves a run behind to group, so there is nothing here to say "not shared" about.</summary>
public sealed partial class MiningDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Mining, "MINING")
{
    public ObservableCollection<ActivityMiningRowViewModel> Rows { get; } = [];

    public ObservableCollection<MiningCharacterGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private bool _hasMining;

    [ObservableProperty] private string _miningText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    public override bool HasContent => HasMining;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        HasMining = detail.MiningEntries.Count > 0;
        // This section's share of TOTAL ISK as the registry counted it (ET-256), not a sum of its own.
        MiningText = IskFormat.WholeOrNoPrice(
            detail.Isk.Of(IskSource.Mining) is { Certainty: not IskCertainty.Unknown } mining ? mining.Amount : null);
        EmptyText = HasMining ? null : "No mining line came past in the game log for this activity.";
        HeaderSummary = HasMining ? $"{MiningText} · {detail.MiningEntries.Count} ore lines" : "nothing measured";
    }

    /// <summary>Prices every ore by exact SDE name, one appraisal call for the whole activity's distinct types
    /// (Mutanite never needs it — <see cref="MiningValuation"/>). Skipped when the activity has no mining at all, or
    /// when the SDE is unavailable.</summary>
    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        if (services.Sde is not { IsAvailable: true } sde || input.Detail.MiningEntries.Count == 0)
        {
            Rows.Clear();
            Groups.Clear();
            return;
        }

        Dictionary<Guid, long> characterByRun = input.Detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        List<(RunMiningEntryDto Entry, int? TypeId)> resolved = [.. input.Detail.MiningEntries
            .Where(entry => characterByRun.ContainsKey(entry.RunId))
            .Select(entry => (entry, sde.TryGetTypeId(entry.OreType, out int id) ? (int?)id : null))];

        Dictionary<int, double> prices = new();
        int[] typeIds = [.. resolved.Select(r => r.TypeId).OfType<int>().Distinct()];
        if (typeIds.Length > 0 && services.Appraisal is { } appraisal)
        {
            Result<AppraisalOutcome> valued = await appraisal.AppraiseAsync(
                [.. typeIds.Select(id => new AppraisalLine(id, string.Empty, 1))], cancellationToken);
            if (valued.IsSuccess)
                foreach (AppraisalRow row in valued.Value!.Rows)
                    if (row.Price?.Estimate is { } estimate)
                        prices[row.Line.TypeId] = estimate;
        }

        Rows.Clear();
        foreach ((RunMiningEntryDto entry, int? typeId) in resolved.OrderByDescending(r => r.Entry.Units))
        {
            decimal? unitPrice = typeId is { } id ? MiningValuation.UnitPrice(sde, id, prices) : null;
            bool isFixedPrice = typeId is { } fixedId && sde.GetType(fixedId)?.GroupId == MiningValuation.MutaniteGroupId;
            Rows.Add(new ActivityMiningRowViewModel(
                entry.RunId, characterByRun[entry.RunId], entry.OreType, entry.Units, entry.CriticalUnits,
                entry.ResidueUnits, unitPrice is { } price ? price * entry.Units : null, isFixedPrice, input.NameOf));
        }

        _SyncGroups(input, resolved, characterByRun, sde, prices);
    }

    private void _SyncGroups(RunDetailSectionInput input, List<(RunMiningEntryDto Entry, int? TypeId)> resolved,
        IReadOnlyDictionary<Guid, long> characterByRun, ISdeAccessor sde, Dictionary<int, double> prices)
    {
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>();
        ILookup<long, ActivityRunDetailDto> runsByCharacter = input.Detail.Runs.ToLookup(run => run.CharacterId);

        List<CharacterBuild> builds = [];
        foreach (IGrouping<long, (RunMiningEntryDto Entry, int? TypeId)> character in
                 resolved.GroupBy(r => characterByRun[r.Entry.RunId]))
        {
            ActivityRunDetailDto[] runs = [.. runsByCharacter[character.Key]];
            DateTime miningStart = character.Min(r => r.Entry.FirstObservedAtUtc);
            DateTime miningEnd = character.Max(r => r.Entry.LastObservedAtUtc);
            DateTime runStart = runs.Min(run => run.StartedAtUtc);
            DateTime runEnd = runs.Max(run => run.StoppedAtUtc ?? run.StartedAtUtc);
            builds.Add(new CharacterBuild(character.Key, input.NameOf(character.Key), own.Contains(character.Key),
                [.. character], miningStart, miningEnd, runStart, runEnd));
        }

        bool isFleetScenario = builds.Count > 1;
        Dictionary<string, int> fleetOreUnits = [];
        foreach (CharacterBuild build in builds)
            foreach ((RunMiningEntryDto entry, int? _) in build.Entries)
                fleetOreUnits[entry.OreType] = fleetOreUnits.GetValueOrDefault(entry.OreType) + entry.Units;

        List<(MiningCharacterGroupViewModel Group, decimal Isk)> built =
            [.. builds.Select(build => _ToGroup(build, sde, prices, fleetOreUnits, isFleetScenario))];

        Groups.Clear();
        foreach (MiningCharacterGroupViewModel group in built
                     .OrderByDescending(entry => entry.Group.IsLocal)
                     .ThenByDescending(entry => entry.Isk)
                     .Select(entry => entry.Group))
            Groups.Add(group);
    }

    private static (MiningCharacterGroupViewModel Group, decimal Isk) _ToGroup(CharacterBuild build, ISdeAccessor sde,
        Dictionary<int, double> prices, IReadOnlyDictionary<string, int> fleetOreUnits, bool isFleetScenario)
    {
        List<(string Ore, int Units, int Crit, int Residue, decimal? Isk, decimal? UnitPrice, bool IsFixedPrice)> lines =
        [
            .. build.Entries.Select(r =>
            {
                decimal? unitPrice = r.TypeId is { } id ? MiningValuation.UnitPrice(sde, id, prices) : null;
                bool isFixedPrice = r.TypeId is { } fixedId && sde.GetType(fixedId)?.GroupId == MiningValuation.MutaniteGroupId;
                return (r.Entry.OreType, r.Entry.Units, r.Entry.CriticalUnits, r.Entry.ResidueUnits,
                    unitPrice is { } price ? price * r.Entry.Units : (decimal?)null, unitPrice, isFixedPrice);
            })
        ];

        decimal? isk = lines.Any(line => line.Isk is not null) ? lines.Sum(line => line.Isk.GetValueOrDefault()) : null;
        int totalResidue = lines.Sum(line => line.Residue);
        decimal residueIsk = lines.Sum(line => (line.UnitPrice ?? 0m) * line.Residue);

        List<ActivityMiningRowViewModel> oreRows = [];
        foreach ((string ore, int units, int crit, int residue, decimal? lineIsk, decimal? _, bool isFixedPrice) in
                 lines.OrderByDescending(line => line.Units))
        {
            double? shareFraction = null;
            string? shareTooltip = null;
            if (isFleetScenario)
            {
                int fleetUnits = fleetOreUnits.GetValueOrDefault(ore);
                shareFraction = fleetUnits > 0 ? (double)units / fleetUnits : 0;
                shareTooltip = $"{build.Name} · {ore} — {IskFormat.Number(units)} of the fleet's " +
                                $"{IskFormat.Number(fleetUnits)} units";
            }
            else if (isk is { } total && total > 0 && lineIsk is not null)
            {
                shareFraction = (double)(lineIsk.Value / total);
                shareTooltip = $"{ore} — part of {build.Name}'s own ISK mix";
            }

            oreRows.Add(new ActivityMiningRowViewModel(Guid.Empty, build.CharacterId, ore, units, crit, residue,
                lineIsk, isFixedPrice, _ => build.Name, shareFraction, shareTooltip));
        }

        string residueText = totalResidue > 0 ? $"{IskFormat.Number(totalResidue)} residue" : "no residue";
        string? residueTooltip = totalResidue > 0
            ? $"{IskFormat.Whole(residueIsk)} lost — ore taken from the rock that never reached the hold"
            : null;

        TimeSpan miningTime = build.MiningEnd - build.MiningStart;
        TimeSpan runTime = build.RunEnd - build.RunStart;
        decimal? miningTimeRate = isk is { } total2 && miningTime > TimeSpan.Zero
            ? total2 / (decimal)miningTime.TotalHours : null;
        decimal? runTimeRate = isk is { } total3 && runTime > TimeSpan.Zero
            ? total3 / (decimal)runTime.TotalHours : null;

        string rateText = miningTimeRate is { } avg ? $"{IskFormat.Compact(avg)}/h avg" : "—/h avg";
        string? rateTooltip = miningTimeRate is null && runTimeRate is null
            ? null
            : $"{(miningTimeRate is { } m ? IskFormat.Whole(m) : "no price yet")}/h over the mining time " +
              $"({_Duration(miningTime)}); {(runTimeRate is { } r ? IskFormat.Whole(r) : "no price yet")}/h over " +
              $"the whole run ({_Duration(runTime)}).";

        return (new MiningCharacterGroupViewModel(build.CharacterId, build.Name, build.IsLocal, isk, rateText,
            rateTooltip, residueText, residueTooltip, null, null, oreRows), isk ?? 0m);
    }

    private static string _Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}m" : $"{(int)span.TotalMinutes}m{span.Seconds:D2}s";

    public override string AbsentReason(string noun) => $"no MINING — {noun} has nothing mined in your game log";

    private sealed record CharacterBuild(
        long CharacterId, string Name, bool IsLocal, IReadOnlyList<(RunMiningEntryDto Entry, int? TypeId)> Entries,
        DateTime MiningStart, DateTime MiningEnd, DateTime RunStart, DateTime RunEnd);
}
