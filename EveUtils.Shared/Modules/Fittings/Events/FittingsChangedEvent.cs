using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;

namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>
/// A fit library on this machine changed: the local library on a client (a fit stored by a paste, a clipboard offer, an
/// ESI import or a download, removed or edited — ET-382, ET-383), or the shared library on a server (ET-383). Published
/// on the local bus once the write is done, so whatever lists the library re-reads however the change was made. Never
/// on the wire itself: a server's relay turns a shared-library change into the <c>fittings.shared</c> and
/// <c>fittings.deleted</c> pushes the clients listen for.
/// </summary>
/// <param name="fitId">The fit a single-fit change was about; null for an import of several.</param>
/// <param name="characterId">The character who made the change, where one did.</param>
public sealed class FittingsChangedEvent(FittingsChangeKind kind, int count, int? fitId = null, int? characterId = null)
    : IntegrationEvent<FittingsChangedData>(new FittingsChangedData(kind, count, fitId), characterId)
{
    public override string EventType => "fittings.changed";
}

/// <param name="Count">How many fits the change touched.</param>
public sealed record FittingsChangedData(FittingsChangeKind Kind, int Count, int? FitId);
