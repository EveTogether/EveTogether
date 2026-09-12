using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// The bounty each run's own character earned during it, read back from that character's gamelog (ET-271). For a run
/// that did not exist while the site was flown — a sibling <see cref="SetRunAttendanceCommand"/> added once the list said
/// an own character was in the site (ET-269): live, a bounty line only lands on a run running for its character, so
/// every line that character earned went nowhere. A line already recorded on any run of the same character is never
/// read in a second time. Returns how many lines were added.
/// </summary>
public sealed record ImportRunBountyCommand(IReadOnlyList<Guid> RunIds) : ICommand<Result<int>>;
