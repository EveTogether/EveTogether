using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>
/// A clipboard copy has just been filed against the run carrying <see cref="IntegrationEvent{T}.Data"/>. Local
/// only: the capture is this machine's own reading of its own clipboard, and nothing on another machine has a use
/// for it.
///
/// It exists because the window that shows a run's loot is not the thing that records it. The capture arrives on
/// the clipboard watch, is stored by <c>AddRunLootCaptureCommandHandler</c>, and until this event there was nothing
/// to tell an already-open activity window to look again — so the toast said the copy had been recorded while the
/// LOOT section under it still read "no loot captured" (Raymond, 2026-09-02).
/// </summary>
public sealed class RunLootCapturedEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.loot-captured";
}
