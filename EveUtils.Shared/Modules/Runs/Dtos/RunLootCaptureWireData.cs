using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunLootCaptureWireData
{
    public required DateTime CapturedAtUtc { get; init; }
    public required LootCaptureSource Source { get; init; }
    public LootCaptureRole Role { get; init; }
    public string? ContentHash { get; init; }
    public required bool IsExcluded { get; init; }
    public required IReadOnlyList<RunLootEntryInput> Entries { get; init; }

    /// <summary>Not required: a payload from a client older than ET-460 simply has none.</summary>
    public IReadOnlyList<UnrecognisedLootLineInput> UnrecognisedLines { get; init; } = [];
}
