using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// MINING in the run window: what each participant's own gamelog has mined so far, per ore (ET-229) — general for
/// any mining, not only a homefront's. Priced through the price source, Mutanite at its fixed NPC price
/// (<see cref="MiningValuation"/>). Contributes a share of TOTAL ISK — see <see cref="IskSource.Mining"/>.
///
/// <see cref="FleetMinedText"/> and <see cref="RemainingText"/> (ET-234) add what the rest of the fleet shares
/// (<c>RunShareUpdate</c>, ET-242's wire) on top of this window's own rows — the same "counted from what members
/// share" honesty <see cref="FleetWindowSectionViewModel.FleetBasisText"/> already states for loot and bounty.
/// </summary>
public sealed class MiningWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Mining, "MINING")
{
    // Ore prices barely move mid-run and there are only ever a handful of distinct ores on one run, so every priced
    // type id is kept for the life of the section rather than re-asked once it has an answer.
    private readonly Dictionary<int, double> _prices = new();

    public ObservableCollection<ActivityMiningRowViewModel> Rows { get; } = [];

    /// <summary>The whole fleet's mined units so far — this window's own rows plus whoever else shares (ET-234).
    /// Null while there is nothing to say at all: no own mining and nobody sharing.</summary>
    public string? FleetMinedText { get; private set; }

    /// <summary>What is left of a Metaliminal Meteoroid's 5,000-unit asteroid (<see cref="IRunWindowContext.RunType"/>'s
    /// own <c>SiteMiningCapacityUnits</c>) — null for any site whose capacity is not a known figure, including an
    /// ordinary mining fleet and AAR (ET-234).</summary>
    public string? RemainingText { get; private set; }

    public override void Refresh(DateTime nowUtc)
    {
        _SyncRows();
        _RefreshFleetTotals();
        _ = _RefreshPricesAsync();
    }

    public override void RefreshSummary()
    {
        decimal? total = _Total();
        HeaderSummary = Rows.Count == 0
            ? "no mining measured"
            : total is { } isk
                ? $"{IskFormat.Whole(isk)} — {Rows.Count} ore lines"
                : $"{Rows.Count} ore lines — no price yet";
    }

    /// <summary>One participant's own mining, for the group ISK total (ET-256) — the same shape
    /// <c>ActivityWindowViewModel._ConsumableFacts</c> reads from CONSUMABLES.</summary>
    public (decimal? Value, bool Has) FactsFor(Guid runId)
    {
        ActivityMiningRowViewModel[] rows = [.. Rows.Where(row => row.RunId == runId)];
        if (rows.Length == 0)
            return (null, false);

        return rows.Any(row => row.Value is not null) ? (rows.Sum(row => row.Value.GetValueOrDefault()), true) : (null, true);
    }

    private decimal? _Total() => Rows.Any(row => row.Value is not null) ? Rows.Sum(row => row.Value.GetValueOrDefault()) : null;

    /// <summary>This run's own total units mined (crit included), for <see cref="MetricKind.MiningYield"/>'s
    /// producer — the same per-run sum <see cref="FactsFor"/> already keeps for ISK, in units instead.</summary>
    public int UnitsFor(Guid runId) => Rows.Where(row => row.RunId == runId).Sum(row => row.Units);

    /// <summary>
    /// The whole fleet's mining, own rows plus whoever else shares (ET-234) — never this window's own characters
    /// twice, the same de-duplication <see cref="LootWindowSectionViewModel"/> applies against
    /// <see cref="IRunWindowContext.Participants"/>.
    ///
    /// Crit is counted into <see cref="FleetMinedText"/> as real ore in the hold, but not subtracted from
    /// <see cref="RemainingText"/>'s capacity — a crit yield takes nothing extra from the asteroid
    /// (domain/homefronts.md §6.1). Treating it as consumed anyway is a deliberate, conservative simplification: it
    /// slightly under-reports what is left rather than over-promise it, and it spares the wire a third field for an
    /// effect measured at under 2% of mining lines.
    /// </summary>
    private void _RefreshFleetTotals()
    {
        int units = Rows.Sum(row => row.Units);
        int residue = Rows.Sum(row => row.ResidueUnits);
        bool hasShared = false;

        if (Context.GroupCode is { } groupCode && Context.FleetId is { } fleetId
            && Context.Services.GetService<FleetRunShares>() is { } shares)
        {
            HashSet<int> own = [.. Context.Participants.Select(participant => participant.CharacterId)];
            foreach ((int characterId, RunShareUpdate share) in shares.Of(groupCode))
            {
                if (share.FleetId != fleetId || !share.SharesMining || own.Contains(characterId))
                    continue;

                units += share.MinedUnits;
                residue += share.ResidueUnits;
                hasShared = true;
            }
        }

        FleetMinedText = Rows.Count == 0 && !hasShared
            ? null
            : $"fleet mined {IskFormat.Number(units)} units. Counted from what members share — a member sharing " +
              "nothing is missing from this total.";

        RemainingText = Context.RunType.SiteMiningCapacityUnits is { } capacity && FleetMinedText is not null
            ? $"~{IskFormat.Number(Math.Max(0, capacity - units - residue))} units remaining in the site. Only as " +
              "accurate as what the fleet shares."
            : null;

        OnPropertyChanged(nameof(FleetMinedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    /// <summary>Rebuilt every tick from <see cref="IRunWindowContext.Participants"/>, which already carries each
    /// one's own <c>RunMiningEntry</c> rows (ET-229) — read-only rows (unlike CONSUMABLES' editable count), so a full
    /// rebuild each tick is simpler than reconciling by identity and costs nothing a handful of ore lines can't take.</summary>
    private void _SyncRows()
    {
        ISdeAccessor? sde = Context.Services.GetService<ISdeAccessor>();
        Rows.Clear();
        foreach (RunParticipantViewModel participant in Context.Participants)
            foreach (RunMiningOreDto entry in participant.MiningEntries.OrderByDescending(e => e.Units))
            {
                int? typeId = sde is { IsAvailable: true } && sde.TryGetTypeId(entry.OreType, out int id) ? id : null;
                decimal? unitPrice = typeId is { } tid ? MiningValuation.UnitPrice(sde!, tid, _prices) : null;
                bool isFixedPrice = typeId is { } fixedId && sde!.GetType(fixedId)?.GroupId == MiningValuation.MutaniteGroupId;
                Rows.Add(new ActivityMiningRowViewModel(
                    participant.RunId, participant.CharacterId, entry.OreType, entry.Units, entry.CriticalUnits,
                    entry.ResidueUnits, unitPrice is { } price ? price * entry.Units : null, isFixedPrice,
                    _ => participant.CharacterName));
            }

        RefreshSummary();
    }

    /// <summary>Prices every distinct ore not yet priced, one appraisal call for the whole set.</summary>
    private async Task _RefreshPricesAsync()
    {
        if (Context.Services.GetService<ISdeAccessor>() is not { IsAvailable: true } sde
            || Context.Services.GetService<IAppraisalProvider>() is not { } appraisal)
            return;

        int[] typeIds = [.. Context.Participants.SelectMany(p => p.MiningEntries).Select(e => e.OreType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ore => sde.TryGetTypeId(ore, out int id) ? (int?)id : null)
            .OfType<int>()
            .Where(id => !_prices.ContainsKey(id))
            .Distinct()];
        if (typeIds.Length == 0)
            return;

        Result<AppraisalOutcome> valued = await appraisal.AppraiseAsync(
            [.. typeIds.Select(id => new AppraisalLine(id, string.Empty, 1))]);
        if (!valued.IsSuccess)
            return;

        foreach (AppraisalRow row in valued.Value!.Rows)
            if (row.Price?.Estimate is { } estimate)
                _prices[row.Line.TypeId] = estimate;

        _SyncRows();
    }
}
