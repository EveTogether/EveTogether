using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootCapture
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public LootCaptureSource Source { get; set; }
    public Run? Run { get; set; }
    public ICollection<RunLootEntry> Entries { get; } = [];
}
