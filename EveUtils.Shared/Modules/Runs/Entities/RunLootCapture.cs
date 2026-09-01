using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootCapture
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public LootCaptureSource Source { get; set; }

    /// <summary>SHA256 over the raw clipboard text — the same window copied twice carries the same hash.</summary>
    public string? ContentHash { get; set; }

    /// <summary>A repeat of an earlier capture is stored excluded rather than dropped: silently deduplicating is as
    /// wrong as silently double counting, and only a visible capture can be put back in.</summary>
    public bool IsExcluded { get; set; }
    public Run? Run { get; set; }
    public ICollection<RunLootEntry> Entries { get; } = [];
}
