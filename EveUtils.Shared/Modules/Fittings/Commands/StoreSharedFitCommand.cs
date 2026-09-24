using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Adds a fit to this server's shared library, unless the same fit is already in it (content-hash dedup). Returns how
/// many fits were stored: 0 with a <c>DUPLICATE</c> message naming the match. The caller has checked <c>fit.sync</c>;
/// the gRPC share and the event-bus gate each do before they get here.
/// </summary>
public sealed record StoreSharedFitCommand(FitSharedPayload Fit, int SharedByCharacterId) : ICommand<Result<int>>;
