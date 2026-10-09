using EveUtils.Shared.Modules.Gamelog.Aggregation;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Where a room boundary came from, as mockup (a) shows it (ET-368): the AUTO badge and the certainty dots.
/// Only a detected room has one; a room the pilot set shows nothing extra. A fleet member's room that came from the
/// commander's list says FC instead, with the dots only when the commander's detector found it (ET-494).</summary>
public sealed class RoomSourceViewModel(RoomCertainty? certainty, bool isFromCommander = false)
{
    public string Badge { get; } = isFromCommander ? "FC" : "AUTO";

    public bool HasCertainty { get; } = certainty is not null;

    public bool IsSure { get; } = certainty is RoomCertainty.Sure;

    public string Dots { get; } = certainty is RoomCertainty.Sure ? "●●" : "●○";

    public string Word { get; } = certainty?.ToString().ToLowerInvariant() ?? string.Empty;

    public static RoomSourceViewModel? Of(RoomCertainty? certainty, bool isFromCommander = false) =>
        certainty is not null || isFromCommander ? new(certainty, isFromCommander) : null;
}
