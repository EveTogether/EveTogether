using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Undoes <see cref="DeleteRunCommand"/> on one run — the soft-delete's whole point (ET-214): a delete the
/// pilot did not mean can be put straight back without a second read of anything.</summary>
public sealed record RestoreRunCommand(Guid RunId) : ICommand<Result>;
