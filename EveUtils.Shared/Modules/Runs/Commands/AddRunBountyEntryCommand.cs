using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Hangs one bounty payout on the run running now for this character — the live counterpart to
/// <see cref="AddRunLootCaptureCommand"/>, so a payout survives a crash or an unsaved run instead of living only in
/// <c>GamelogClientService</c>'s in-memory tally until SAVE (ET-219). <see cref="CharacterId"/> scopes the running-run
/// lookup to this pilot's own run exactly the way a loot capture's does; null falls back to the system-wide count.</summary>
public sealed record AddRunBountyEntryCommand(long? CharacterId, DateTime OccurredAtUtc, decimal Isk) : ICommand<Result>;
