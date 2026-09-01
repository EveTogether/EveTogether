using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.Fleet;

/// <summary>
/// What a screen knows about one fleet member, gathered so the shared member menu can be built the same way
/// everywhere. Deliberately all-nullable where a screen may not know a fact: fleet metrics knows the live sample
/// facts, the roster knows the structure facts, and a fact nobody knows produces a line that says so rather than a
/// line that guesses. <paramref name="LastSampleAt"/> null means no metric sample has ever arrived for this pilot on
/// this screen — which is exactly the "is this pilot still with us" question the menu exists to answer.
/// </summary>
public sealed record FleetMemberFacts(
    string MemberName,
    FleetRole Role,
    bool IsExternal,
    string? ShipName = null,
    string? FitName = null,
    string? Location = null,
    bool IsWithCommander = false,
    DateTimeOffset? LastSampleAt = null,
    bool TracksLiveMetrics = false,
    FleetMemberPresenceState Presence = FleetMemberPresenceState.Unknown);
