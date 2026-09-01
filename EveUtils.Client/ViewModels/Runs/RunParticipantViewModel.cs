using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character on a homefront run, carrying the two facts that must never become one boolean (ET-105).
///
/// <see cref="IsParticipant"/> is "did they fly this site". <see cref="IsPayoutEligible"/> is "do they take a share
/// of what we split". The hauler who fetched ore while the other five ran the site is the case that forces them
/// apart: participant, loot registered, no ISK. Folded together, "did not fly it" and "flew it unpaid" could not be
/// told apart afterwards.
/// </summary>
public sealed partial class RunParticipantViewModel : ObservableObject
{
    public RunParticipantViewModel(Guid runId, int characterId, string characterName,
        bool isParticipant = true, bool isPayoutEligible = true)
    {
        RunId = runId;
        CharacterId = characterId;
        CharacterName = characterName;
        _isParticipant = isParticipant;
        _isPayoutEligible = isPayoutEligible;
    }

    public Guid RunId { get; }

    public int CharacterId { get; }

    public string CharacterName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayoutDisplay))]
    [NotifyPropertyChangedFor(nameof(StandingText))]
    private bool _isParticipant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayoutDisplay))]
    [NotifyPropertyChangedFor(nameof(StandingText))]
    private bool _isPayoutEligible;

    /// <summary>Null until there is a figure to divide; zero is never used to mean "not known".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayoutDisplay))]
    private decimal? _payoutIsk;

    /// <summary>
    /// An excluded pilot reads "0 ISK — excluded from the split", never a dash or a blank (ET-105 AC-3): a zero
    /// somebody chose and a figure nobody has must not look the same on screen.
    /// </summary>
    public string PayoutDisplay => !IsPayoutEligible
        ? "0 ISK — excluded from the split"
        : PayoutIsk is { } isk
            ? $"{isk:N2} ISK"
            : "no figure yet";

    /// <summary>Both flags said out loud, because the interesting row is the one where they disagree.</summary>
    public string StandingText => (IsParticipant, IsPayoutEligible) switch
    {
        (true, true) => "flew the site · takes a share",
        (true, false) => "flew the site · no share",
        (false, true) => "did not fly the site · takes a share",
        (false, false) => "did not fly the site · no share"
    };
}
