using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>The result of the create/edit-fleet dialog. Null from the dialog = cancelled.</summary>
public sealed record FleetEditResult(
    string Name,
    string? Description,
    FleetVisibility Visibility,
    DateTimeOffset? FromTime,
    DateTimeOffset? ToTime);
