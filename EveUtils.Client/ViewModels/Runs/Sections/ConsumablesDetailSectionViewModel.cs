using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Runs.Tally;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>CONSUMABLES on the detail screen: per run, the filament its pilot confirmed at SAVE and whatever else he
/// wrote out as spent, each a loot line valued by type id (ET-249, ET-329, ET-334), and the way to rewrite it by hand.
/// The total is the registry's own share of TOTAL ISK (ET-256) — never a total of its own.</summary>
public sealed partial class ConsumablesDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Consumables, "CONSUMABLES")
{
    private readonly Dictionary<int, decimal> _unitPrices = [];

    /// <summary>Per run, the game log's hit tallies ET-467 kept at SAVE: the check beside a fired charge (ET-471).</summary>
    private readonly Dictionary<Guid, IReadOnlyList<RunHitTallyDto>> _hitTallies = [];

    public ObservableCollection<ConsumablesCharacterViewModel> Characters { get; } = [];

    [ObservableProperty] private bool _hasConsumables;

    [ObservableProperty] private string _costText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    public override bool HasContent => HasConsumables;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        (ActivityRunDetailDto Run, ActivityLootLineViewModel[] Lines, SpentChargeLineViewModel[] Charges)[] spentByRun =
            [.. detail.Runs.Select(run => (run, _SpentLines(detail, run), _SpentCharges(run)))];

        HasConsumables = spentByRun.Any(spent => spent.Lines.Length > 0 || spent.Charges.Length > 0);
        // This section's share of TOTAL ISK as the registry counted it (ET-256) — already negative, a cost.
        decimal? cost = detail.Isk.Of(IskSource.Consumables) is { Certainty: not IskCertainty.Unknown } share
            ? share.Amount
            : null;
        CostText = cost is { } isk ? IskFormat.Whole(isk) : "no figure yet";
        _ShowCharacters(input, spentByRun);

        int filaments = detail.Runs.Sum(run => _FilamentCount(detail, run.RunId));
        long others = detail.Runs.SelectMany(_Spent).Sum(entry => entry.Quantity ?? 1);
        long charges = spentByRun.SelectMany(spent => spent.Charges).Sum(charge => charge.Line.Quantity ?? 1);
        List<string> summary = [CostText];
        if (filaments > 0)
            summary.Add(filaments == 1 ? "1 filament" : $"{filaments} filaments");
        if (others > 0)
            summary.Add(others == 1 ? "1 other item" : $"{others} other items");
        if (charges > 0)
        {
            summary.Add(charges == 1 ? "1 charge fired" : $"{charges} charges fired");
        }
        EmptyText = Characters.Count == 0 ? "No filament count or other consumable was confirmed for this activity." : null;
        HeaderSummary = HasConsumables ? string.Join(" · ", summary) : "nothing confirmed";
    }

    /// <summary>Prices every line by type id from the cached ESI average price, the way LOOT does, then draws the
    /// lines again with those prices and their icons.</summary>
    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        _unitPrices.Clear();
        await _LoadHitTalliesAsync(input.Detail, cancellationToken);
        await _LoadPricesAsync(Characters.SelectMany(_AllLines).Select(line => line.ItemTypeId), cancellationToken);
        Apply(input);

        if (services.Images is not { } images)
            return;

        foreach (ActivityLootLineViewModel line in Characters.SelectMany(_AllLines).Where(line => line.ItemTypeId > 0))
        {
            await line.LoadIconAsync(images);
        }
    }

    public override string AbsentReason(string noun) => $"no CONSUMABLES — {noun} used no tracked consumable";

    /// <summary>A block for every run that spent something, and for every run of the pilot's own that did not, so
    /// he can say what it did spend. A block already on screen stays the same one, so a box open in it keeps what is
    /// typed while another section's correction reads the activity again.</summary>
    private void _ShowCharacters(RunDetailSectionInput input,
        IReadOnlyList<(ActivityRunDetailDto Run, ActivityLootLineViewModel[] Lines, SpentChargeLineViewModel[] Charges)> spentByRun)
    {
        List<ConsumablesCharacterViewModel> wanted = [];
        foreach ((ActivityRunDetailDto run, ActivityLootLineViewModel[] lines, SpentChargeLineViewModel[] charges) in spentByRun)
        {
            bool isReadOnly = services.OwnCharacterIds is { } own && !own.Contains(run.CharacterId);
            if (lines.Length == 0 && charges.Length == 0 && isReadOnly)
            {
                continue;
            }

            ConsumablesCharacterViewModel block = Characters.FirstOrDefault(character => character.RunId == run.RunId)
                                                  ?? _NewBlock(run, input.NameOf(run.CharacterId), isReadOnly);
            block.Show(lines, charges);
            wanted.Add(block);
        }

        if (Characters.SequenceEqual(wanted))
            return;

        Characters.Clear();
        foreach (ConsumablesCharacterViewModel block in wanted)
            Characters.Add(block);
    }

    private ConsumablesCharacterViewModel _NewBlock(ActivityRunDetailDto run, string characterText, bool isReadOnly) =>
        new(services.Dispatcher, services.Sde, run.RunId, characterText, isReadOnly, _OnStoredAsync);

    /// <summary>A correction reads the activity again without this section's own <see cref="LoadAsync"/>, so a type the
    /// rewritten list brought in is priced here first — otherwise a drone just added would read "no price".</summary>
    private async Task _OnStoredAsync(IReadOnlyList<int> typeIds)
    {
        await _LoadPricesAsync(typeIds.Where(typeId => !_unitPrices.ContainsKey(typeId)), CancellationToken.None);
        RaiseActivityCorrected();
    }

    private async Task _LoadPricesAsync(IEnumerable<int> typeIds, CancellationToken cancellationToken)
    {
        int[] wanted = [.. typeIds.Where(typeId => typeId > 0).Distinct()];
        if (wanted.Length == 0)
        {
            return;
        }

        List<AppraisalLine> lines = [.. wanted.Select(typeId => new AppraisalLine(typeId, string.Empty, 1))];
        // ET-364: the selector (the user's chosen provider, with a fallback to ESI average) takes priority;
        // services.Appraisal only still matters for a caller that never set Services.
        Result<AppraisalOutcome> valued;
        if (services.Services?.GetService<IAppraisalProviderSelector>() is { } selector)
        {
            valued = await selector.AppraiseWithFallbackAsync(lines, cancellationToken);
        }
        else if (services.Appraisal is { } appraisal)
        {
            valued = await appraisal.AppraiseAsync(lines, cancellationToken);
        }
        else
        {
            return;
        }

        if (!valued.IsSuccess || valued.Value is not { } outcome)
            return;

        foreach (AppraisalRow row in outcome.Rows)
            if (row.Price is { } price)
                _unitPrices[row.Line.TypeId] = (decimal)price.Estimate;
    }

    /// <summary>What one run spent: its confirmed filament first, then what its pilot wrote out beside it, most
    /// valuable first. Each at the price the run fixed for it (ET-463), the live one only while it has none.</summary>
    private ActivityLootLineViewModel[] _SpentLines(ActivityDetailDto detail, ActivityRunDetailDto run)
    {
        List<ActivityLootLineViewModel> lines = [];
        if (_FilamentCount(detail, run.RunId) is > 0 and var count)
        {
            (int typeId, string name) = _FilamentOf(detail, run.RunId);
            decimal? fixedPrice = detail.Parameters.FirstOrDefault(parameter => parameter.RunId == run.RunId
                && parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId)?.UnitPriceIsk;
            lines.Add(_Line(typeId, name, count, fixedPrice));
        }

        Dictionary<int, decimal> fixedPrices = FixedLootPrices.Of(run.LootCaptures);
        lines.AddRange(_Spent(run)
            .Select(entry => _Line(entry.ItemTypeId, entry.Name, entry.Quantity,
                fixedPrices.TryGetValue(entry.ItemTypeId, out decimal kept) ? kept : null))
            .OrderByDescending(line => line.Value.HasValue)
            .ThenByDescending(line => line.Value));
        return [.. lines];
    }

    /// <summary>The charges a run's two cargo holds show were fired (ET-471), split off by the same rule the run's
    /// TOTAL ISK is (<see cref="LootTally"/>), at the price the run fixed for each. None without a starting hold.</summary>
    private SpentChargeLineViewModel[] _SpentCharges(ActivityRunDetailDto run)
    {
        Dictionary<int, decimal> fixedPrices = FixedLootPrices.Of(run.LootCaptures);
        Dictionary<int, string> names = run.LootCaptures.SelectMany(capture => capture.Entries)
            .GroupBy(entry => entry.ItemTypeId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        IReadOnlyList<RunHitTallyDto> tallies = _hitTallies.GetValueOrDefault(run.RunId) ?? [];
        LootTallyCapture[] captures = [.. run.LootCaptures
            .OrderBy(capture => capture.CapturedAtUtc)
            .Select(capture => new LootTallyCapture(capture.Role, capture.IsExcluded,
                [.. capture.Entries.Select(entry => new LootTallyLine(entry.ItemTypeId, entry.Quantity, null, entry.LootKind))]))];
        return [.. LootTally.Count(captures, ChargeTypes.Of(services.Sde)).SpentCharges
            .Select(charge => _Line(charge.ItemTypeId, names[charge.ItemTypeId], charge.Quantity,
                fixedPrices.TryGetValue(charge.ItemTypeId, out decimal kept) ? kept : null))
            .Select(line => new SpentChargeLineViewModel(line, _GamelogHits(tallies, line.Name)))];
    }

    /// <summary>How many outgoing hits the game log names this charge as the weapon of, or null when it names none:
    /// turret ammo never has a line of its own.</summary>
    private static int? _GamelogHits(IReadOnlyList<RunHitTallyDto> tallies, string chargeName)
    {
        RunHitTallyDto[] own = [.. tallies.Where(tally => tally.Direction is DamageDirection.Outgoing
            && tally.Quality is not HitQuality.Misses && tally.Weapon == chargeName)];
        return own.Length == 0 ? null : own.Sum(tally => tally.Count);
    }

    private async Task _LoadHitTalliesAsync(ActivityDetailDto detail, CancellationToken cancellationToken)
    {
        _hitTallies.Clear();
        foreach (ActivityRunDetailDto run in detail.Runs)
        {
            if (await services.Dispatcher.Query(new GetRunCombatTimelineQuery(run.RunId), cancellationToken)
                is { IsSuccess: true, Value: { } timeline })
            {
                _hitTallies[run.RunId] = timeline.HitTallies;
            }
        }
    }

    private static IEnumerable<ActivityLootLineViewModel> _AllLines(ConsumablesCharacterViewModel character) =>
        character.Lines.Concat(character.SpentCharges.Select(charge => charge.Line));

    private static IEnumerable<RunLootEntryDto> _Spent(ActivityRunDetailDto run) => run.LootCaptures
        .Where(capture => capture.Role is LootCaptureRole.Consumed && !capture.IsExcluded)
        .SelectMany(capture => capture.Entries);

    private static int _FilamentCount(ActivityDetailDto detail, Guid runId) =>
        detail.Parameters.FirstOrDefault(parameter => parameter.RunId == runId
                                                      && parameter.ParameterKey == RunParameterKey.AbyssalFilamentCount)
            is { } stored && int.TryParse(stored.TypedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? count
            : 0;

    private decimal? _UnitPrice(int typeId) => _unitPrices.TryGetValue(typeId, out decimal price) ? price : null;

    private ActivityLootLineViewModel _Line(int typeId, string name, long? quantity, decimal? fixedPrice) =>
        new(typeId, name, quantity, fixedPrice ?? _UnitPrice(typeId), LootKind.Lost, isLivePrice: fixedPrice is null);

    /// <summary>The filament a run was saved against: the type id SAVE stored, named as the SDE names it, and the
    /// pocket's own tier and weather when the SDE has no such type — a saved run never guesses a type.</summary>
    private (int TypeId, string Name) _FilamentOf(ActivityDetailDto detail, Guid runId)
    {
        RunParameterDto[] own = [.. detail.Parameters.Where(parameter => parameter.RunId == runId)];
        int typeId = own.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId)
            is { } stored && int.TryParse(stored.TypedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
        string name = typeId > 0 && services.Sde?.GetType(typeId)?.Name is { } sdeName
            ? sdeName
            : $"{AbyssalFilamentName.From(own.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue)} Filament";
        return (typeId, name);
    }
}
