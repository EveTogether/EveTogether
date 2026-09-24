using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>What a saved run's pilot spent, as he wrote it out (ET-334) — the loot rewrite's twin. The run's own
/// filament becomes its confirmed filament count, zero when the list leaves it out; everything else is stored as one
/// capture with role <see cref="Enums.LootCaptureRole.Consumed"/>, so it travels to a server the way loot does.
/// Writing again replaces both.</summary>
public sealed record SetRunConsumablesManualCommand(Guid RunId, IReadOnlyList<RunLootEntryInput> Entries)
    : ICommand<Result>;
