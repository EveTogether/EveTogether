using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One character's run in the live activity, carrying the two facts that must never become one boolean (ET-105).
///
/// <see cref="IsParticipant"/> is "did they fly this site". <see cref="IsPayoutEligible"/> is "do they take a part of
/// the loot split" — FLEET's one-click exception for the hauler (ET-272). Folded together, "did not fly it" and "flew
/// it without a share" could not be told apart afterwards.
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

    [ObservableProperty] private bool _isParticipant;

    [ObservableProperty] private bool _isPayoutEligible;

    /// <summary>This run's own <c>RunBountyEntry</c> total (ET-219), refreshed alongside <see cref="IsParticipant"/>
    /// — the live window's TOTAL ISK sums this across every participant regardless of a fleet (ET-257), the same
    /// figure a saved activity already counts.</summary>
    [ObservableProperty]
    private decimal _bountyIsk;

    /// <summary>This run's own <c>RunMiningEntry</c> rows (ET-229), refreshed alongside <see cref="BountyIsk"/> —
    /// general for any mining, never only a homefront's.</summary>
    [ObservableProperty]
    private IReadOnlyList<RunMiningOreDto> _miningEntries;

    /// <summary>The homefront attendance tick (ET-230), null while nobody decided — which is every run before it and
    /// every run that is not a homefront.</summary>
    [ObservableProperty] private bool? _inSiteAtCompletion;

    /// <summary>N (ET-231) — the same on every run of the group, carried per run like <see cref="InSiteAtCompletion"/>
    /// since this is a flat list of runs and not a separate group-level fact.</summary>
    [ObservableProperty] private int? _attendanceCount;

    /// <summary>How the homefront ended (ET-231) — null while nobody has said, and not used for Abyssal Artifact
    /// Recovery (see <see cref="HomefrontCompletedWaveCount"/>).</summary>
    [ObservableProperty] private HomefrontOutcome? _homefrontOutcome;

    /// <summary>Abyssal Artifact Recovery's own outcome (ET-231): how many of its 9 waves paid out.</summary>
    [ObservableProperty] private int? _homefrontCompletedWaveCount;

    /// <summary>A homefront payout the pilot typed over the table's for this run (ET-271), as stored — null while
    /// they typed none.</summary>
    [ObservableProperty] private decimal? _fixedPayoutIsk;
}
