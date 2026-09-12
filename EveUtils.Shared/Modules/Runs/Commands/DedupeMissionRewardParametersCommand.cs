using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// One-time repair for ET-260: before that fix, a mission flown with more than one own toon wrote the identical
/// reward parameters (ISK, BonusIsk, LP, …) onto every character's own run in the group, because every one of them
/// received the same <c>StartRunCommand.Parameters</c> list. EVE itself only ever pays the character who accepted the
/// mission — the source fix stops writing them onto anyone else's run; this sweeps every activity that was already
/// saved with the duplicate before that fix landed.
///
/// Keep-rule: within a duplicated <see cref="Enums.ActivityKind.Mission"/> group, the run with the most bounty keeps
/// the parameters — in practice the character who actually fought, which in the measured case (Jithran's own
/// Angel Extravaganza, group HF-WR43) is also the one who activated the mission (29,976,751 ISK bounty against the
/// salvage alt's own, far smaller share). Ties broken by <c>RunId</c> for a stable, deterministic pick. Every other
/// run in the group loses its copy outright, and a published one is marked <see cref="Enums.RunSyncState.Outdated"/>
/// (ET-215's own rule) so the correction reaches the server on the next publish.
/// </summary>
public sealed record DedupeMissionRewardParametersCommand : ICommand<Result<int>>;
