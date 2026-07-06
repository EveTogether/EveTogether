using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Pushes a locally stored fitting to ESI for the given character.
/// For local chars the client calls ESI directly. Returns the new ESI fitting_id.
/// </summary>
public sealed record PushFittingToEsiCommand(
    int CharacterId,
    string AccessToken,
    int LocalFittingId) : ICommand<Result<int>>;
