using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Hangs one mining cycle's ore on the run running now for this character — the live counterpart to
/// <see cref="AddRunBountyEntryCommand"/> (ET-229). <see cref="CharacterId"/> scopes the running-run lookup to this
/// pilot's own run the same way a bounty payout's does. Required: a gamelog line without an id never falls back to the
/// system-wide count, or another character's mining lands on whatever single run happens to be running (ET-422).
/// <see cref="Units"/> is zero for a residue-only correction (the residue line names no ore of its own).</summary>
public sealed record AddRunMiningEntryCommand(
    long CharacterId, DateTime OccurredAtUtc, string OreType, int Units, bool IsCritical, int ResidueUnits)
    : ICommand<Result>;
