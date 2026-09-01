using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunLootCaptureInput
{
    public required DateTime CapturedAtUtc { get; init; }
    public required LootCaptureSource Source { get; init; }
    public required IReadOnlyList<RunLootEntryInput> Entries { get; init; }
}
