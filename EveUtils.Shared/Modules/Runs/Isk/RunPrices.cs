using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// The prices one run is valued at (ET-463): the unit price fixed on its own lines where there is one, and the live
/// cache price only where a line has none yet. One type has one price per run — the earliest capture's — because the
/// tally nets lines across captures by type before anything is valued.
/// </summary>
internal sealed class RunPrices
{
    private readonly IReadOnlyDictionary<int, decimal> _fixed;
    private readonly IReadOnlyDictionary<int, double> _live;
    private readonly decimal? _fixedFilament;

    private RunPrices(IReadOnlyDictionary<int, decimal> fixedPrices, decimal? fixedFilament, IReadOnlyDictionary<int, double> live)
    {
        _fixed = fixedPrices;
        _fixedFilament = fixedFilament;
        _live = live;
    }

    /// <summary>The run must come with its loot captures and their entries loaded.</summary>
    public static RunPrices Of(Run run, IEnumerable<RunParameter> parameters, IReadOnlyDictionary<int, double> live) =>
        new(FixedLootPrices(run.LootCaptures), FilamentRow(parameters)?.UnitPriceIsk, live);

    /// <summary>The unit price per type the run's own lines already fixed, earliest capture first.</summary>
    public static IReadOnlyDictionary<int, decimal> FixedLootPrices(IEnumerable<RunLootCapture> captures)
    {
        Dictionary<int, decimal> prices = [];
        foreach (RunLootEntry entry in captures.OrderBy(capture => capture.CapturedAtUtc).SelectMany(capture => capture.Entries))
            if (entry.UnitPriceIsk is { } price)
                prices.TryAdd(entry.ItemTypeId, price);
        return prices;
    }

    /// <summary>The row a run's filament type is saved on (ET-249), where its price is fixed too.</summary>
    public static RunParameter? FilamentRow(IEnumerable<RunParameter> parameters) =>
        parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId);

    public decimal? Loot(int typeId) =>
        _fixed.TryGetValue(typeId, out decimal price) ? price : _Live(typeId);

    public decimal? Filament(int typeId) => _fixedFilament ?? _Live(typeId);

    private decimal? _Live(int typeId) => _live.TryGetValue(typeId, out double price) ? (decimal)price : null;
}
