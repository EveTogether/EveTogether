namespace EveUtils.Client.ViewModels.Map;

/// <summary>A system the followed fleet is in, in the fleet card's footer.</summary>
/// <param name="HasCommander">The fleet commander is one of the members here.</param>
public sealed record MapFleetSystemChip(int SystemIndex, string SystemName, int Members, bool HasCommander)
{
    public string? Tip => HasCommander ? "The fleet commander is here" : null;

    public string Text => Members == 1 ? SystemName : $"{SystemName} ×{Members}";
}
