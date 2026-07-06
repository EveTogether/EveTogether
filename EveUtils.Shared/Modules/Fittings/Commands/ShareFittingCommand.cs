using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Shares a locally stored fitting via the server. Publishes a <c>FitSharedEvent</c>
/// with <c>EventTarget.Both</c>: stored locally for display and sent to the server, which persists
/// it in <c>SharedFit</c> and re-routes to other connected clients.
/// Gated by <c>fit.sync</c> app-permission.
/// </summary>
public sealed record ShareFittingCommand(
    int LocalFittingId,
    int OwnerCharacterId,
    string OwnerCharacterName) : ICommand<Result>;
