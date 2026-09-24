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
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>CONSUMABLES on the detail screen: per run, the filament its pilot confirmed at SAVE and whatever else he
/// wrote out as spent, each a loot line valued by type id (ET-249, ET-329, ET-334), and the way to rewrite it by hand.
/// The total is the registry's own share of TOTAL ISK (ET-256) — never a total of its own.</summary>
public sealed partial class ConsumablesDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Consumables, "CONSUMABLES")
{
    private readonly Dictionary<int, decimal> _unitPrices = [];

    public ObservableCollection<ConsumablesCharacterViewModel> Characters { get; } = [];

    [ObservableProperty] private bool _hasConsumables;

    [ObservableProperty] private string _costText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    public override bool HasContent => HasConsumables;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        (ActivityRunDetailDto Run, ActivityLootLineViewModel[] Lines)[] spentByRun =
            [.. detail.Runs.Select(run => (run, _SpentLines(detail, run)))];

        HasConsumables = spentByRun.Any(spent => spent.Lines.Length > 0);
        // This section's share of TOTAL ISK as the registry counted it (ET-256) — already negative, a cost.
        decimal? cost = detail.Isk.Of(IskSource.Consumables) is { Certainty: not IskCertainty.Unknown } share
            ? share.Amount
            : null;
        CostText = cost is { } isk ? IskFormat.Whole(isk) : "no figure yet";
        _ShowCharacters(input, spentByRun);

        int filaments = detail.Runs.Sum(run => _FilamentCount(detail, run.RunId));
        long others = detail.Runs.SelectMany(_Spent).Sum(entry => entry.Quantity ?? 1);
        List<string> summary = [CostText];
        if (filaments > 0)
            summary.Add(filaments == 1 ? "1 filament" : $"{filaments} filaments");
        if (others > 0)
            summary.Add(others == 1 ? "1 other item" : $"{others} other items");
        EmptyText = Characters.Count == 0 ? "No filament count or other consumable was confirmed for this activity." : null;
        HeaderSummary = HasConsumables ? string.Join(" · ", summary) : "nothing confirmed";
    }

    /// <summary>Prices every line by type id from the cached ESI average price, the way LOOT does, then draws the
    /// lines again with those prices and their icons.</summary>
    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        _unitPrices.Clear();
        await _LoadPricesAsync(Characters.SelectMany(character => character.Lines).Select(line => line.ItemTypeId),
            cancellationToken);
        Apply(input);

        if (services.Images is not { } images)
            return;

        foreach (ActivityLootLineViewModel line in Characters.SelectMany(character => character.Lines).Where(line => line.ItemTypeId > 0))
            await line.LoadIconAsync(images);
    }

    public override string AbsentReason(string noun) => $"no CONSUMABLES — {noun} used no tracked consumable";

    /// <summary>A block for every run that spent something, and for every run of the pilot's own that did not, so
    /// he can say what it did spend. A block already on screen stays the same one, so a box open in it keeps what is
    /// typed while another section's correction reads the activity again.</summary>
    private void _ShowCharacters(RunDetailSectionInput input,
        IReadOnlyList<(ActivityRunDetailDto Run, ActivityLootLineViewModel[] Lines)> spentByRun)
    {
        List<ConsumablesCharacterViewModel> wanted = [];
        foreach ((ActivityRunDetailDto run, ActivityLootLineViewModel[] lines) in spentByRun)
        {
            bool isReadOnly = services.OwnCharacterIds is { } own && !own.Contains(run.CharacterId);
            if (lines.Length == 0 && isReadOnly)
                continue;

            ConsumablesCharacterViewModel block = Characters.FirstOrDefault(character => character.RunId == run.RunId)
                                                  ?? _NewBlock(run, input.NameOf(run.CharacterId), isReadOnly);
            block.Show(lines);
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
        if (services.Appraisal is not { } appraisal || wanted.Length == 0)
            return;

        Result<AppraisalOutcome> valued = await appraisal.AppraiseAsync(
            [.. wanted.Select(typeId => new AppraisalLine(typeId, string.Empty, 1))], cancellationToken);
        if (!valued.IsSuccess || valued.Value is not { } outcome)
            return;

        foreach (AppraisalRow row in outcome.Rows)
            if (row.Price is { } price)
                _unitPrices[row.Line.TypeId] = (decimal)price.Estimate;
    }

    /// <summary>What one run spent: its confirmed filament first, then what its pilot wrote out beside it, most
    /// valuable first.</summary>
    private ActivityLootLineViewModel[] _SpentLines(ActivityDetailDto detail, ActivityRunDetailDto run)
    {
        List<ActivityLootLineViewModel> lines = [];
        if (_FilamentCount(detail, run.RunId) is > 0 and var count)
        {
            (int typeId, string name) = _FilamentOf(detail, run.RunId);
            lines.Add(new ActivityLootLineViewModel(typeId, name, count, _UnitPrice(typeId), LootKind.Lost));
        }

        lines.AddRange(_Spent(run)
            .Select(entry => new ActivityLootLineViewModel(entry.ItemTypeId, entry.Name, entry.Quantity,
                _UnitPrice(entry.ItemTypeId), LootKind.Lost))
            .OrderByDescending(line => line.Value.HasValue)
            .ThenByDescending(line => line.Value));
        return [.. lines];
    }

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
