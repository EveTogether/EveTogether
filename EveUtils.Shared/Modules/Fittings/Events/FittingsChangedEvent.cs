using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Enums;

namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>
/// The fit library on this machine changed (ET-382): a fit was stored by a paste, a clipboard offer or an ESI import.
/// Published on the local bus once the write is done, so a screen listing the library re-reads however the fit got
/// there. Never on the wire: it describes this machine's database.
/// </summary>
public sealed class FittingsChangedEvent(FittingsChangeKind kind, int count)
    : IntegrationEvent<FittingsChangedData>(new FittingsChangedData(kind, count))
{
    public override string EventType => "fittings.changed";
}

/// <param name="Count">How many fits the change stored.</param>
public sealed record FittingsChangedData(FittingsChangeKind Kind, int Count);
