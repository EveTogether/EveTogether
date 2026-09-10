using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>
/// Something stored about a run changed — any run, any field, any writer (ET-222). The one signal a screen that shows
/// runs listens to, so it never has to know which command touched what: the specific events beside it (saved, deleted,
/// loot captured…) stay for listeners that act on the reason, and this one is for everything that only re-reads.
///
/// Every command handler in <c>Modules/Runs/Commands</c> that writes publishes it once its write — and any summary
/// rebuild it runs — is done; <c>RunsChangedSignalCoverageTests</c> fails for a new command that does not. Local only:
/// it describes this machine's database, which no other machine reads.
/// </summary>
public sealed class RunsChangedEvent(Guid? runId, string? groupCode = null)
    : IntegrationEvent<RunsChangedEventData>(new RunsChangedEventData(runId, groupCode))
{
    public override string EventType => "runs.changed";
}
