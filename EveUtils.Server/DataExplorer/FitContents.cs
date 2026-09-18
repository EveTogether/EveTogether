using System.Text.Json;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// What a shared fit holds, read from its stored ESI JSON. There are no module tables on the server; the JSON is the
/// record. A fit whose JSON cannot be read fails here with a message the pane shows, instead of taking the list down.
/// </summary>
public sealed class FitContents
{
    public required IReadOnlyList<FitContentItem> Items { get; init; }

    public static Result<FitContents> Parse(string rawJson)
    {
        EsiFitting? fitting;
        try
        {
            fitting = JsonSerializer.Deserialize<EsiFitting>(rawJson);
        }
        catch (JsonException ex)
        {
            return _Unreadable($"The stored JSON is not a valid ESI fitting: {ex.Message}");
        }

        if (fitting?.Items is null)
            return _Unreadable("The stored JSON has no item list.");
        if (fitting.Items.Any(i => i is null))
            return _Unreadable("The stored item list has an empty entry.");

        List<FitContentItem> items = fitting.Items
            .GroupBy(i => (Slot: _SlotOf(i.Flag), i.TypeId))
            .Select(g => new FitContentItem { Slot = g.Key.Slot, TypeId = g.Key.TypeId, Quantity = g.Sum(i => Math.Max(1, i.Quantity)) })
            .OrderBy(i => i.Slot)
            .ToList();
        return Result<FitContents>.Success(new FitContents { Items = items });
    }

    // ESI numbers the slots it names ("HiSlot0".."HiSlot7"); the bays and cargo have one flag each.
    private static FitSlot _SlotOf(string? flag) => flag switch
    {
        null => FitSlot.Other,
        _ when flag.StartsWith("HiSlot", StringComparison.Ordinal) => FitSlot.High,
        _ when flag.StartsWith("MedSlot", StringComparison.Ordinal) => FitSlot.Mid,
        _ when flag.StartsWith("LoSlot", StringComparison.Ordinal) => FitSlot.Low,
        _ when flag.StartsWith("RigSlot", StringComparison.Ordinal) => FitSlot.Rig,
        _ when flag.StartsWith("SubSystemSlot", StringComparison.Ordinal) => FitSlot.Subsystem,
        "DroneBay" => FitSlot.Drones,
        "FighterBay" => FitSlot.Fighters,
        "Cargo" => FitSlot.Cargo,
        _ => FitSlot.Other,
    };

    private static Result<FitContents> _Unreadable(string text) => Result<FitContents>.Failure(
        new ResultMessage(MessageSeverity.Error, MessageCodes.ParseError, text, "Panel"));
}
