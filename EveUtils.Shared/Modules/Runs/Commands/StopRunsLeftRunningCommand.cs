using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Bring to rest every run still on the clock from a previous process, and answer with how many there were.
///
/// A run outlives its window, and <see cref="SetRunStoppedCommand"/> only ever fires while the app is up: quit with
/// one running — or lose the process — and the row stays <see cref="Enums.RunState.Running"/> with nobody left to
/// stop it. The next window then adopts it, which is how Raymond opened the app on 2026-09-04 and was shown a run
/// that had started the previous morning, reading ELAPSED 1467:38.
///
/// Sent once at startup, before any window exists to adopt one. Deliberately a stop and not a save or a discard:
/// what became of that run is the pilot's call, and this only ends the thing that was falsely still running.
/// </summary>
public sealed record StopRunsLeftRunningCommand(DateTime StoppedAtUtc) : ICommand<Result<int>>;
