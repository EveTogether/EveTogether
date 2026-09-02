using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One loot entry. Its value is looked up by <c>ItemTypeId</c>: the clipboard ISK column is still parsed
/// and kept as what that window happened to show, but nothing is valued from it (Raymond, 2026-09-02).</summary>
public sealed record RunLootEntryDto(int ItemTypeId, string Name, long? Quantity, decimal? ClipboardPrice, LootKind LootKind);

/// <summary>One clipboard snapshot on the running run, with its exclusion flag so the reader can tell a kept-but-
/// excluded repeat from a counted capture.</summary>
public sealed record RunLootCaptureDto(
    Guid CaptureId, DateTime CapturedAtUtc, bool IsExcluded, string? ContentHash,
    IReadOnlyList<RunLootEntryDto> Entries);

/// <summary>The running run's loot snapshots, oldest first.</summary>
public sealed record RunLootOverview(Guid RunId, IReadOnlyList<RunLootCaptureDto> Captures);
