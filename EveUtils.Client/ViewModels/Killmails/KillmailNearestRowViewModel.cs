namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One line of the killmail's NEAREST list (ET-473). <see cref="SecurityText"/> is the destination's security
/// for a stargate, null for anything else.</summary>
public sealed record KillmailNearestRowViewModel(
    string Name, string KindText, string DistanceText, string? SecurityText, bool IsNearest);
