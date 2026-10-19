using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Client.Transport;

/// <summary>The anonymous run outcomes of one server (ET-514), as one of the pilots coupled to it. Apart from the
/// run sync on purpose: its own call, its own payload, nothing shared with publishing runs.</summary>
public interface IServerRunOutcomeClient
{
    /// <summary>Stores or replaces the outcome of <paramref name="activityRef"/> at <paramref name="revision"/>, or with
    /// <paramref name="outcome"/> null takes it back (the tombstone).</summary>
    Task<OutcomeCallResult> ShareAsync(string serverAddress, long characterId, string activityRef, long revision,
        RunOutcomeShareDto? outcome, CancellationToken cancellationToken = default);

    /// <summary>The opt-out: removes everything <paramref name="characterId"/> shared with the server.</summary>
    Task<OutcomeCallResult> WithdrawAsync(string serverAddress, long characterId, CancellationToken cancellationToken = default);
}

/// <param name="Code">The server's machine-readable refusal, empty when accepted.</param>
/// <param name="IsTransient">The call never got an answer worth keeping (no connection, no session, a server error):
/// worth trying again later. A refusal with a code is the server's answer and is not.</param>
public sealed record OutcomeCallResult(bool Accepted, string Code, string Message, RunOutcomeShareStatus? Status = null,
    int Count = 0, bool IsTransient = false)
{
    public static OutcomeCallResult Transient(string message) => new(false, string.Empty, message, IsTransient: true);
}
