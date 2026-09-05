using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunLootCaptureInput
{
    public required DateTime CapturedAtUtc { get; init; }
    public required LootCaptureSource Source { get; init; }
    public LootCaptureRole Role { get; init; }
    public string? ContentHash { get; init; }

    /// <summary>The run the caller already knows this belongs to — the open activity window's own, never guessed.
    /// Null when there is no better question than <see cref="EveUtils.Shared.Modules.Runs.Commands.RunningRunLookup"/>'s.</summary>
    public Guid? PreferredRunId { get; init; }
    public required IReadOnlyList<RunLootEntryInput> Entries { get; init; }
}
