namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>What saving one clipboard snapshot did. <see cref="RepeatOfCapturedAtUtc"/> is the earlier capture's
/// time when this one is a byte-identical repeat and was therefore stored excluded, and null when it counts
/// towards the run's totals.</summary>
public sealed record RunLootCaptureSaveResult(Guid CaptureId, DateTime? RepeatOfCapturedAtUtc);
