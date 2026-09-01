using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootEntry
{
    public Guid Id { get; set; }
    public Guid RunLootCaptureId { get; set; }
    public int ItemTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? Quantity { get; set; }
    public decimal? Volume { get; set; }
    public decimal? ClipboardPrice { get; set; }
    public LootKind LootKind { get; set; }
    public RunLootCapture? RunLootCapture { get; set; }
}
