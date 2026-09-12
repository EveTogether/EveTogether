using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

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
        bool isParticipant = true, bool isPayoutEligible = true, decimal bountyIsk = 0m,
        IReadOnlyList<RunMiningOreDto>? miningEntries = null)
    {
        RunId = runId;
        CharacterId = characterId;
        CharacterName = characterName;
        _isParticipant = isParticipant;
        _isPayoutEligible = isPayoutEligible;
        _bountyIsk = bountyIsk;
        _miningEntries = miningEntries ?? [];
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

    /// <summary>This run's own <c>RunBountyEntry</c> total (ET-219), refreshed alongside <see cref="IsParticipant"/>
    /// — the live window's TOTAL ISK sums this across every participant regardless of a fleet (ET-257), the same
    /// figure a saved activity already counts.</summary>
    [ObservableProperty]
    private decimal _bountyIsk;

    /// <summary>This run's own <c>RunMiningEntry</c> rows (ET-229), refreshed alongside <see cref="BountyIsk"/> —
    /// general for any mining, never only a homefront's.</summary>
    [ObservableProperty]
    private IReadOnlyList<RunMiningOreDto> _miningEntries;

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
            ? IskFormat.Whole(isk)
            : "no figure yet";

    /// <summary>The homefront attendance tick (ET-230), null while nobody decided — which is every run before it and
    /// every run that is not a homefront.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StandingText))]
    private bool? _inSiteAtCompletion;

    /// <summary>N (ET-231) — the same on every run of the group, carried per run like <see cref="InSiteAtCompletion"/>
    /// since this is a flat list of runs and not a separate group-level fact.</summary>
    [ObservableProperty] private int? _attendanceCount;

    /// <summary>How the homefront ended (ET-231) — null while nobody has said, and not used for Abyssal Artifact
    /// Recovery (see <see cref="HomefrontCompletedWaveCount"/>).</summary>
    [ObservableProperty] private HomefrontOutcome? _homefrontOutcome;

    /// <summary>Abyssal Artifact Recovery's own outcome (ET-231): how many of its 9 waves paid out.</summary>
    [ObservableProperty] private int? _homefrontCompletedWaveCount;

    /// <summary>Both flags said out loud, because the interesting row is the one where they disagree.</summary>
    public string StandingText => Describe(IsParticipant, IsPayoutEligible, InSiteAtCompletion);

    /// <summary>
    /// The two ET-105 facts in words. Once a homefront's attendance is decided (ET-230), "flew the site" would stand
    /// beside HOMEFRONT's "not in site" for the hauler and contradict it, so the first half then says only that the
    /// character has a run in the group — whether they were in the site is HOMEFRONT's to say. A run nobody decided
    /// attendance for, every run before it, reads exactly as it always did.
    /// </summary>
    public static string Describe(bool isParticipant, bool isPayoutEligible, bool? inSiteAtCompletion)
    {
        string flew = inSiteAtCompletion is not null ? "in the group"
            : isParticipant ? "flew the site" : "did not fly the site";
        return $"{flew} · {(isPayoutEligible ? "takes a share" : "no share")}";
    }
}
