using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>
/// The loot of the SAVED run carrying <see cref="IntegrationEvent{T}.Data"/> was corrected after the fact (ET-215),
/// and its activity's summary has already been rebuilt by the time this fires. Local only, like
/// <see cref="RunSavedEvent"/>: it is for the screens on this machine that show day totals and "ISK today", which
/// otherwise only move when a run is saved.
/// </summary>
public sealed class RunLootCorrectedEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.loot-corrected";
}
