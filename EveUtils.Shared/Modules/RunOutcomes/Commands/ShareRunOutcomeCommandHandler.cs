using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Permissions.Repositories;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.RunOutcomes.Events;
using EveUtils.Shared.Modules.RunOutcomes.Repositories;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Commands;

internal sealed class ShareRunOutcomeCommandHandler(IRunOutcomeRepository repository, IPermissionToggleStore toggles,
    IEventBus eventBus) : ICommandHandler<ShareRunOutcomeCommand, Result<RunOutcomeShareStatus>>
{
    private const int MaximumLootLines = 2000;
    private const int MaximumFleetSize = 256;
    private const decimal MaximumFixedIsk = 1_000_000_000_000_000m;

    public async Task<Result<RunOutcomeShareStatus>> Handle(ShareRunOutcomeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (RunOutcomesGate.Refusal(toggles) is { } refusal)
            return Result<RunOutcomeShareStatus>.Failure(refusal);

        RunOutcomeShareDto shared = command.Outcome;
        if (RunOutcomeKeys.Normalised(shared.Key) is not { } key)
            return _Invalid("The outcome names no activity the statistic keys on.");

        if (_Problem(shared) is { } problem)
            return _Invalid(problem);

        RunOutcome outcome = new()
        {
            ActivityId = command.ActivityId,
            ContributorBucket = command.ContributorBucket,
            KeyCode = key.Code,
            CompletedOn = shared.CompletedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            // Down to the whole minute: what is left still falls on the same side of the statistic's 60 s minimum.
            DurationSeconds = shared.DurationSeconds / 60 * 60,
            FixedIsk = shared.FixedIsk,
            FleetSize = shared.FleetSize,
            LootBasis = shared.LootBasis,
            OpenUnrecognisedLootCount = shared.OpenUnrecognisedLootCount,
            Revision = command.Revision,
            LootLines = [.. shared.LootLines
                .GroupBy(line => (line.Kind, line.TypeId))
                .Select(type => new RunOutcomeLootLine
                {
                    Kind = type.Key.Kind, TypeId = type.Key.TypeId, Quantity = type.Sum(line => line.Quantity)
                })]
        };
        RunOutcomeKeys.Apply(outcome, key);

        RunOutcomeShareStatus status = await repository.UpsertAsync(outcome, cancellationToken);
        if (status is RunOutcomeShareStatus.Stored or RunOutcomeShareStatus.Updated)
            await eventBus.PublishAsync(new RunOutcomesChangedEvent(
                    status is RunOutcomeShareStatus.Stored ? RunOutcomesChangeKind.Stored : RunOutcomesChangeKind.Updated, 1, outcome.Id),
                EventTarget.Local, cancellationToken);

        return Result<RunOutcomeShareStatus>.Success(status);
    }

    private static string? _Problem(RunOutcomeShareDto shared)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (shared.CompletedOn > today.AddDays(1) || shared.CompletedOn < today.AddDays(-RunOutcomesModule.WidestWindowDays))
            return "The outcome's day is outside the statistic's window.";
        if (shared.DurationSeconds < 0)
            return "The outcome's duration is negative.";
        if (shared.FixedIsk is < 0 or > MaximumFixedIsk)
            return "The outcome's ISK is out of range.";
        if (shared.FleetSize is < 1 or > MaximumFleetSize)
            return "The outcome's fleet size is out of range.";
        if (!Enum.IsDefined(shared.LootBasis) || shared.OpenUnrecognisedLootCount < 0)
            return "The outcome's loot basis is not readable.";
        if (shared.LootLines.Count > MaximumLootLines)
            return "The outcome carries too many loot lines.";
        if (shared.LootLines.Any(line => !Enum.IsDefined(line.Kind) || line.TypeId <= 0 || line.Quantity <= 0))
            return "A loot line names no item or no quantity.";
        return null;
    }

    private static Result<RunOutcomeShareStatus> _Invalid(string text) =>
        Result<RunOutcomeShareStatus>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, text,
            "RunOutcomes"));
}
