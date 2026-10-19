using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

/// <summary>How many outcomes the server holds, for the control panel: a count and nothing about who shared them.</summary>
public sealed record CountRunOutcomesQuery : IQuery<Result<int>>;
