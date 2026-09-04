using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>What one of the two paste boxes now holds. Rewrites the capture the box already made rather than hanging
/// a second one on the run, because pasting again is a correction of the same cargo hold and not a new observation
/// of it — that is what keeps "paste, paste again, change your mind" from leaving a trail of rows behind.</summary>
public sealed record SetRunCargoHoldCommand(
    Guid RunId, LootCaptureRole Role, DateTime CapturedAtUtc, IReadOnlyList<RunLootEntryInput> Entries)
    : ICommand<Result<Guid>>;
