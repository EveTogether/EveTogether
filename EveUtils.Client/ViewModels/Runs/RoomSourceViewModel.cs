using EveUtils.Shared.Modules.Gamelog.Aggregation;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>Where a room boundary came from, as mockup (a) shows it (ET-368): the AUTO badge and the certainty dots.
/// Only a detected room has one; a room the pilot set shows nothing extra.</summary>
public sealed class RoomSourceViewModel(RoomCertainty certainty)
{
    public bool IsSure { get; } = certainty is RoomCertainty.Sure;

    public string Dots { get; } = certainty is RoomCertainty.Sure ? "●●" : "●○";

    public string Word { get; } = certainty.ToString().ToLowerInvariant();

    public static RoomSourceViewModel? Of(RoomCertainty? certainty) => certainty is { } detected ? new(detected) : null;
}
