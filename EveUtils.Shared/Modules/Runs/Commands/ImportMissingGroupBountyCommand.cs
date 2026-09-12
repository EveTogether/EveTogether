using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Run at startup: every saved run of this client's own characters that carries no bounty at all while another run of
/// its group does is read against its character's gamelog once (<see cref="ImportRunBountyCommand"/>) — the runs
/// ET-269 added to a group after the site, whose character's bounty never had a run to land on. A run whose gamelog
/// holds nothing for it is remembered and never read for again. Returns how many bounty lines were added.
/// </summary>
public sealed record ImportMissingGroupBountyCommand : ICommand<Result<int>>;
