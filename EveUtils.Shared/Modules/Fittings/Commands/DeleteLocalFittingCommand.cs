using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>Removes a fit from the local library. Touches neither EVE nor any server.</summary>
public sealed record DeleteLocalFittingCommand(int FittingId) : ICommand<Result>;
