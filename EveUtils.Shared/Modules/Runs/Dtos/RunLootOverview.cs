namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One loot entry as the clipboard column showed it — <see cref="ClipboardPrice"/> is that column, not a
/// valuation, and null means the window showed no price for the row (never treated as 0).</summary>
public sealed record RunLootEntryDto(string Name, long? Quantity, decimal? ClipboardPrice);

/// <summary>One clipboard snapshot on the running run, with its exclusion flag so the reader can tell a kept-but-
/// excluded repeat from a counted capture.</summary>
public sealed record RunLootCaptureDto(
    Guid CaptureId, DateTime CapturedAtUtc, bool IsExcluded, string? ContentHash,
    IReadOnlyList<RunLootEntryDto> Entries);

/// <summary>The running run's loot snapshots, oldest first.</summary>
public sealed record RunLootOverview(Guid RunId, IReadOnlyList<RunLootCaptureDto> Captures);
