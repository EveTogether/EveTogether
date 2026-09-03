using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Bring a run's clock to rest in the store, or set it going again — <paramref name="StoppedAtUtc"/> null resumes.
///
/// One command for both directions because STOP is a pause: pressing START again picks the same row back up rather
/// than opening a second one beside it, and a resume that lived somewhere else would be a second idea of what a
/// stop is. Until this existed a stop was only ever a property on the view model: the row stayed
/// <see cref="Enums.RunState.Running"/> for the rest of the session, so every window that opened afterwards adopted
/// it — with its start time, its site and its commander's group code (Raymond, 2026-09-03).
/// </summary>
public sealed record SetRunStoppedCommand(Guid RunId, DateTime? StoppedAtUtc) : ICommand<Result>;
