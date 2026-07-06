using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Commands;

/// <summary>Persists a single combat hit (owner-stamped). Gated by <c>gamelog.record</c>.</summary>
[RequiresPermission(GamelogPermissions.Record)]
public sealed record RecordCombatCommand(int? CharacterId, int Amount, DamageDirection Direction, string Target, DateTimeOffset At)
    : ICommand<Result>;
