using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

public sealed record RebuildActivitySummariesCommand(
    /// <summary>Rebuild only the activity this run belongs to, or every activity when null. A correction to one saved
    /// run (ET-215) changes one summary; a full rebuild scans and prices every saved run in the store, which is the
    /// cost that made a group SAVE take five to six seconds (ET-210).</summary>
    Guid? ActivityOfRunId = null,
    /// <summary>Rebuild only when a summary was built by another set of ISK sources than the one registered now
    /// (<c>IskContributors.Signature</c>) — the startup check that brings activities saved before a source existed up
    /// to date. Anything else leaves the store untouched and returns zero.</summary>
    bool OnlyWhenOutdated = false) : ICommand<Result<int>>;
