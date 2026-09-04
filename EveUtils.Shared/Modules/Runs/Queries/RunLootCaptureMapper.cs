using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The one place a <see cref="RunLootCapture"/> becomes a <see cref="RunLootCaptureDto"/>, shared by every
/// query that reads captures back — the running run's and a named run's alike — so they can't drift into two
/// different shapes for the same entity.</summary>
internal static class RunLootCaptureMapper
{
    public static RunLootCaptureDto ToDto(RunLootCapture capture) => new(
        capture.Id, capture.CapturedAtUtc, capture.IsExcluded, capture.ContentHash,
        [.. capture.Entries.Select(entry => new RunLootEntryDto(entry.ItemTypeId, entry.Name, entry.Quantity, entry.ClipboardPrice, entry.LootKind))]);
}
