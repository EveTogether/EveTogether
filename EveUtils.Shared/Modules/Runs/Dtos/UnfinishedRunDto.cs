using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="StoppedAtUtc">Null when the clock was never brought to rest — a row that reached
/// <see cref="Enums.RunState.Stopped"/> through a path that did not stamp it.</param>
/// <param name="TotalIsk">This run's own earnings so far, added up the same way <c>TotalIskCalculator</c> adds up a
/// saved activity's or a live window's (ET-217): bounty, priced loot net of what was lost, and ISK-shaped rewards.
/// Bounty is written to storage as it is earned, not only at SAVE (ET-219), so this includes it for any run
/// stopped since that fix shipped. A run left unfinished before then has no <c>RunBountyEntry</c> rows to read and
/// reads zero here regardless of what it actually earned — that gap cannot be closed after the fact. Meaningless
/// when <paramref name="TotalIskUnknown"/> is true — read that first.</param>
/// <param name="TotalIskUnknown">True when <see cref="TotalIsk"/> is not a real zero but an unanswered question: the
/// run captured loot and none of it has a known market price, with no bounty or ISK-shaped reward to fall back on
/// either — so there is nothing honest to add up yet, and showing "0 ISK" would claim the run earned nothing when it
/// might not have (ET-217 review, 2026-09-10). A run that captured no loot at all is a real zero, not this.</param>
public sealed record UnfinishedRunDto(
    Guid RunId,
    long CharacterId,
    ActivityKind ActivityKind,
    string? SiteName,
    // The scanner's own group text for this site (ET-226) — resolved into the TYPE this row shows the same way
    // every other run-list row does.
    string? SignatureGroupSnapshot,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc,
    decimal TotalIsk,
    bool TotalIskUnknown);
