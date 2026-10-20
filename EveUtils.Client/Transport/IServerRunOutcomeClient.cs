using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Earnings;

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

    /// <summary>The shared outcomes of <paramref name="key"/> since <paramref name="since"/>, as the server's pilots
    /// shared them (ET-514): weights, no buckets. Below the server's minimum of pilots the answer carries no outcomes
    /// and says so in its availability.</summary>
    Task<OutcomeReadResult<ActivityOutcomesDto>> GetOutcomesAsync(string serverAddress, long characterId, ActivityStatsKey key,
        DateOnly since, CancellationToken cancellationToken = default);

    /// <summary>The loot lines of the outcomes <see cref="GetOutcomesAsync"/> answers for the same key and day, in the
    /// same order.</summary>
    Task<OutcomeReadResult<ActivityOutcomeLootDto>> GetOutcomeLootAsync(string serverAddress, long characterId,
        ActivityStatsKey key, DateOnly since, CancellationToken cancellationToken = default);

    /// <summary>The activities with shared outcomes since <paramref name="since"/>, with how many runs and pilots stand
    /// behind each (ET-517). Only activities at least the server's minimum of pilots contribute to are listed, so a key
    /// listed here is one <see cref="GetOutcomesAsync"/> will answer for.</summary>
    Task<OutcomeReadResult<IReadOnlyList<RunOutcomeKeyCountDto>>> ListOutcomeKeysAsync(string serverAddress, long characterId,
        DateOnly since, CancellationToken cancellationToken = default);
}

/// <param name="Code">The server's machine-readable refusal, empty when accepted.</param>
/// <param name="IsTransient">The call never got an answer worth keeping (no connection, no session, a server error):
/// worth trying again later. A refusal with a code is the server's answer and is not.</param>
public sealed record OutcomeCallResult(bool Accepted, string Code, string Message, RunOutcomeShareStatus? Status = null,
    int Count = 0, bool IsTransient = false)
{
    public static OutcomeCallResult Transient(string message) => new(false, string.Empty, message, IsTransient: true);
}

/// <param name="Value">What the server answered, null when it refused or never answered.</param>
/// <param name="IsTransient">Same meaning as <see cref="OutcomeCallResult.IsTransient"/>.</param>
public sealed record OutcomeReadResult<T>(T? Value, string Code, string Message, bool IsTransient = false) where T : class
{
    public bool Accepted => Value is not null;

    public static OutcomeReadResult<T> Transient(string message) => new(null, string.Empty, message, true);
}
