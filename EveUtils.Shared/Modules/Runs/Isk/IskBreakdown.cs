using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// What each source added to one run or activity, or took from it, and the TOTAL ISK that is their sum (ET-256).
/// Built only by <see cref="IskContributors"/>: every screen that shows a total shows <see cref="Total"/> of one of
/// these and adds up nothing of its own.
/// </summary>
public sealed record IskBreakdown(IReadOnlyList<IskContribution> Contributions)
{
    public static IskBreakdown None { get; } = new([]);

    public decimal Total => Contributions.Sum(contribution => contribution.Amount);

    /// <summary>Whether there is a total to show at all. Nothing but <see cref="IskCertainty.Unknown"/> parts is no
    /// total: "0 ISK" there would read as a valuation that came out at nothing.</summary>
    public bool HasFigure => Contributions.Any(contribution => contribution.Certainty is not IskCertainty.Unknown);

    /// <summary>Something was collected, none of it can be valued yet, and nothing else earned anything.</summary>
    public bool IsUnvalued => Contributions.Count > 0 && !HasFigure;

    /// <summary>Part of <see cref="Total"/> is owed rather than paid (ET-231).</summary>
    public bool HasExpectedPart => Contributions.Any(contribution => contribution.Certainty is IskCertainty.Expected);

    public IskContribution? Of(IskSource source) =>
        Contributions.FirstOrDefault(contribution => contribution.Source == source);

    // By content, not by list reference: the runs overview keeps a row on screen for as long as its DTO compares
    // equal (ET-222), and a breakdown read back from storage is always a new list.
    public bool Equals(IskBreakdown? other) =>
        other is not null && Contributions.SequenceEqual(other.Contributions);

    public override int GetHashCode() =>
        Contributions.Aggregate(0, (hash, contribution) => HashCode.Combine(hash, contribution));
}
