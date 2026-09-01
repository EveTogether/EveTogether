using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary><paramref name="RecordFormerGroup"/> separates a discard from the arbiter's relink: re-arbitration
/// unlinks and relinks constantly, and stamping the audit value there would overwrite the group that was discarded.</summary>
public sealed record UnlinkRunFromGroupCodeCommand(Guid RunId, bool RecordFormerGroup = false) : ICommand<Result>;
