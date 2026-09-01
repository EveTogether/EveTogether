namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// How the run's ISK is expected to divide. Every figure this produces is an expectation, and the window says so
/// (see <see cref="ExpectationLabel"/>) rather than presenting it as something that was measured.
/// </summary>
public static class RunPayoutSplit
{
    /// <summary>
    /// The caption every payout figure is shown under. EVE's own rule and our bookkeeping are different things, and
    /// conflating them is the one claim this window must not make: excluding a pilot here does not stop EVE paying
    /// them. What actually arrived is only in the wallet journal.
    /// </summary>
    public const string ExpectationLabel =
        "Expected split — not a measurement. EVE pays every pilot who interacted (1000 damage or 1000 remote "
        + "repair), up to twice the ideal fleet size, so an excluded pilot who fires one shot is still paid by EVE. "
        + "Exclusion here is our own bookkeeping. What was really received is only in the wallet journal.";

    /// <summary>
    /// Divide <paramref name="totalIsk"/> equally over the participants who take a share. An excluded pilot is set
    /// to a real zero rather than left blank: AC-3 asks for a figure somebody chose, not a missing one. The
    /// remainder of an uneven division is not assigned to anybody — this is an estimate, not a ledger.
    /// </summary>
    public static void Apply(IReadOnlyList<RunParticipantViewModel> participants, decimal? totalIsk)
    {
        List<RunParticipantViewModel> sharing =
            [.. participants.Where(participant => participant.IsPayoutEligible)];

        foreach (RunParticipantViewModel participant in participants)
            participant.PayoutIsk = !participant.IsPayoutEligible
                ? 0m
                : totalIsk is { } total && sharing.Count > 0
                    ? total / sharing.Count
                    : null;
    }
}
