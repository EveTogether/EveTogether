namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class UnrecognisedLootNameInput
{
    public required string Name { get; init; }
    public required long Quantity { get; init; }
}
