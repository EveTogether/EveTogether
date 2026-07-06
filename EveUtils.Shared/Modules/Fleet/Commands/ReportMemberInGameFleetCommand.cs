using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// A member's own client confirms (or retracts) that the pilot is in the coupled in-game fleet. Lets the
/// roster reflect who has actually joined the live fleet even when we are not its boss — the boss-only roster read
/// can't see it. Self-only (like the fit verdict): the acting character must BE the member. The result's value
/// reports whether the stored state changed, so the caller broadcasts only real changes.
/// </summary>
public sealed record ReportMemberInGameFleetCommand(
    long MemberId, bool InFleet, int ActingCharacterId) : ICommand<Result<bool>>;
