using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Shared.Modules.Killmails.Queries;

/// <summary>
/// What the fleet of a group run destroyed (ET-373): the stored kills of <paramref name="CharacterIds"/> from
/// <paramref name="StartedUtc"/> to <paramref name="StoppedUtc"/> plus the killmail linker's stop grace (null while a run is
/// still open), one per killmail. A mail with one of those characters as victim is a loss, never a kill. Local data only.
/// </summary>
public sealed record GetRunFleetKillsQuery(IReadOnlyCollection<int> CharacterIds, DateTime StartedUtc, DateTime? StoppedUtc)
    : IQuery<Result<IReadOnlyList<RunFleetKillDto>>>;
