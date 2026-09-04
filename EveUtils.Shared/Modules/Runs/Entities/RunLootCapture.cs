using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootCapture
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public LootCaptureSource Source { get; set; }

    /// <summary>What this capture is to the run — the hold it started from, the hold it ended on, or a moment in
    /// between. One role and one place to set it, so two starting holds are impossible rather than caught.</summary>
    public LootCaptureRole Role { get; set; }

    /// <summary>SHA256 over the raw clipboard text — the same window copied twice carries the same hash.</summary>
    public string? ContentHash { get; set; }

    /// <summary>A repeat of an earlier capture is stored excluded rather than dropped: silently deduplicating is as
    /// wrong as silently double counting, and only a visible capture can be put back in.</summary>
    public bool IsExcluded { get; set; }
    public Run? Run { get; set; }
    public ICollection<RunLootEntry> Entries { get; } = [];
}
