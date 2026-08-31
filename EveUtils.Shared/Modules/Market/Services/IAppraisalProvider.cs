using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Market.Services;

/// <summary>
/// Values a list of items. One seam over price sources that differ in shape: the cached ESI averages are one global
/// figure per type, an external market service quotes buy and sell per hub. Implementations are auto-registered by
/// their lifetime marker, so a consumer injects <c>IEnumerable&lt;IAppraisalProvider&gt;</c> and lets the user pick
/// when there is more than one.
/// </summary>
public interface IAppraisalProvider
{
    /// <summary>Stable key for the provider, for remembering a choice.</summary>
    string Id { get; }

    /// <summary>What the provider is called in the picker.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Values every line. Failure is the expected answer when the source has nothing to say at all — an unfilled
    /// price cache, an unreachable service — so the caller can report that rather than a total of zero. A line the
    /// source simply has no price for comes back as a row with no price, not as a failure.
    /// </summary>
    Task<Result<AppraisalOutcome>> AppraiseAsync(
        IReadOnlyCollection<AppraisalLine> lines, CancellationToken cancellationToken = default);
}
