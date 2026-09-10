using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record RebuildActivitySummariesCommand(
    /// <summary>Rebuild only the activity this run belongs to, or every activity when null. A correction to one saved
    /// run (ET-215) changes one summary; a full rebuild scans and prices every saved run in the store, which is the
    /// cost that made a group SAVE take five to six seconds (ET-210).</summary>
    Guid? ActivityOfRunId = null) : ICommand<Result<int>>;
