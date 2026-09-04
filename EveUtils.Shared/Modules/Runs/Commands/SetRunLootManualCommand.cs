using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>The loot as the pilot wrote it out. Stored as one capture with source
/// <see cref="Enums.LootCaptureSource.Manual"/>, with every capture that fed it excluded — so the tally keeps its one
/// rule and the window and the saved run go on counting the same way. Editing again rewrites that one capture rather
/// than hanging a second on the run.</summary>
public sealed record SetRunLootManualCommand(
    Guid RunId, DateTime CapturedAtUtc, IReadOnlyList<RunLootEntryInput> Entries) : ICommand<Result<Guid>>;
