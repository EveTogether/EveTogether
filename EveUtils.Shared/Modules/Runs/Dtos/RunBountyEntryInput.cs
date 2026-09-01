namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunBountyEntryInput
{
    public required DateTime OccurredAtUtc { get; init; }
    public required decimal Isk { get; init; }
}
