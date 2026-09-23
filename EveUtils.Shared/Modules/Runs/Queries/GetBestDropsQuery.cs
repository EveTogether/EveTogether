using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The most valuable loot these characters' saved runs brought in since <paramref name="FromUtc"/> (ET-324),
/// counted the way the runs themselves count it — the difference between two cargo holds where a starting hold was
/// pasted — so a hold copied twice is never loot twice.</summary>
public sealed record GetBestDropsQuery(DateTime FromUtc, IReadOnlyList<long> CharacterIds, int Take)
    : IQuery<Result<IReadOnlyList<BestDropDto>>>;
