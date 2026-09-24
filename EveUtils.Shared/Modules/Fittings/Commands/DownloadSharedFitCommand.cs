using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>Stores a fit from a server's shared library in the local library, unless the same fit is already there
/// (content-hash dedup). Returns how many fits were stored: 0 with a <c>DUPLICATE</c> message naming the match.</summary>
public sealed record DownloadSharedFitCommand(FitSharedPayload Fit) : ICommand<Result<int>>;
