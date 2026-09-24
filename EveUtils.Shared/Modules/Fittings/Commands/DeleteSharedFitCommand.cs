using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Removes a fit from this server's shared library. The caller has checked the right to: <c>fit.manage</c> for a
/// client, the control panel's Data · Delete for an admin, who acts as no character.
/// </summary>
public sealed record DeleteSharedFitCommand(int SharedFitId, int? ActingCharacterId) : ICommand<Result>;
