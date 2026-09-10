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

    /// <summary>Who copied this, resolved from <c>ClipboardCapture.CopiedByCharacter</c> (ET-138) against the local
    /// character registry — null when the sender is unknown. Known, it scopes
    /// <see cref="EveUtils.Shared.Modules.Runs.Commands.RunningRunLookup"/> to this character's own run instead of
    /// the system-wide answer <see cref="PreferredRunId"/> falls back to, so a copy from character B's client lands
    /// on B's run even while a window for character A is open (ET-130 deel 4 / ET-211).</summary>
    public long? CharacterId { get; init; }
    public required IReadOnlyList<RunLootEntryInput> Entries { get; init; }
}
