using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>
/// A saved run was soft-deleted (ET-214) and its activity's summary has already been rebuilt by the time this
/// fires. Local only, like <see cref="RunSavedEvent"/>: it is for the screens on this machine that show day totals
/// and the runs overview, which otherwise only move when a run is saved or its loot corrected.
/// </summary>
public sealed class RunDeletedEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.deleted";
}
