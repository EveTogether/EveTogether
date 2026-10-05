using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The escalation dialog (ET-125): a site (searched the same way <see cref="ManualRunStartViewModel"/>
/// searches the site catalogue — search field plus result list, no <c>AutoCompleteBox</c>), a destination system as
/// free text, and the remaining time the Agency showed, turned into a deadline. No field here ever carries a
/// default: ET-124 measured one escalation at 23h57m45s remaining, which does not prove every escalation carries a
/// 24-hour window, so the pilot always types what the Agency actually showed (AC-3).
///
/// ET-126: what is typed is also matched against the catalogue by exact name — the same
/// <see cref="ISdeAccessor.FindSitesByExactName"/> and <see cref="SdeSiteDescription.DescribeShared"/> route
/// <c>ClipboardSignatureOffer.MatchSites</c> already uses (its own docstring: "the toast and the window it opens
/// must not answer differently"). A second matcher here would be exactly the third route that comment forbids.</summary>
public sealed partial class EscalationDialogViewModel : ObservableObject
{
    private readonly ISdeAccessor _sde;
    private readonly List<int> _previouslyRegistered;
    private readonly int? _sourceFactionId;

    /// <param name="sde">The site catalogue.</param>
    /// <param name="sourceSites">What the source run's site matched in the catalogue — its faction, when every match
    /// agrees on one, ranks that faction's escalations next (ET-451).</param>
    /// <param name="previouslyRegistered">Escalation dungeon ids registered before from the same source site, most
    /// often first — ranked on top (ET-451). The SDE carries no source→escalation mapping of its own.</param>
    public EscalationDialogViewModel(
        ISdeAccessor sde, IReadOnlyList<SdeSite>? sourceSites = null, IReadOnlyList<int>? previouslyRegistered = null)
    {
        _sde = sde;
        _previouslyRegistered = [.. previouslyRegistered ?? []];
        _sourceFactionId = (sourceSites ?? []).Select(site => site.FactionId).Distinct().ToList() is [{ } faction]
            ? faction
            : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteResults))]
    [NotifyPropertyChangedFor(nameof(HasSiteResults))]
    [NotifyPropertyChangedFor(nameof(CatalogMatches))]
    [NotifyPropertyChangedFor(nameof(CatalogEnrichmentText))]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _siteQuery = string.Empty;

    /// <summary>
    /// Escalation sites only — every one of them before anything is typed, narrowed by what is typed after (ET-451).
    /// Narrowed to the Escalation archetype in code, not trusted to <see cref="ISdeAccessor.SearchSites"/>'s own
    /// archetype filter, which a test double is free to ignore. Ranked: what this store registered from the same
    /// source site before, then the source site's own faction, then by name.
    ///
    /// Built through <see cref="SdeSitePickerOption.From"/> — the one presentation this picker shares with
    /// <see cref="ManualRunStartViewModel"/>'s, so two rows sharing a name (of the 384 Escalation sites, only 64
    /// have a catalogue-wide unique one) are never two unpickable, identical-looking duplicates.
    /// </summary>
    public IReadOnlyList<SdeSitePickerOption> SiteResults =>
        [.. SdeSitePickerOption.From(_EscalationSites(string.IsNullOrWhiteSpace(SiteQuery) ? null : SiteQuery))
            .OrderBy(option => _HistoryRank(option.Site))
            .ThenBy(option => option.Site.FactionId == _sourceFactionId && _sourceFactionId is not null ? 0 : 1)];

    public bool HasSiteResults => SiteResults.Count > 0;

    /// <summary>ET-126: what the typed name resolves to by exact match, across every archetype — unfiltered, so
    /// <see cref="CatalogEnrichmentText"/> can say what a disagreeing pair of matches (e.g. a Combat Site and an
    /// Escalation sharing a name) still agree on.</summary>
    public IReadOnlyList<SdeSite> CatalogMatches =>
        string.IsNullOrWhiteSpace(SiteQuery) ? [] : _sde.FindSitesByExactName(SiteQuery);

    /// <summary>Shows what every exact-name match agrees on and stays silent about the rest — never a guess, never
    /// an error on no match (ET-126 AC-2, AC-3).</summary>
    public string? CatalogEnrichmentText =>
        SdeSiteDescription.DescribeShared(CatalogMatches) is { Length: > 0 } shared ? shared : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSite))]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private SdeSitePickerOption? _selectedOption;

    /// <summary>The site behind the picked option, or null when nothing was picked — what <see cref="Register"/>
    /// and the rest of this type read; the label in <see cref="SelectedOption"/> is display-only.</summary>
    public SdeSite? SelectedSite => SelectedOption?.Site;

    public bool HasSelectedSite => SelectedOption is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationResolvedSystem))]
    [NotifyPropertyChangedFor(nameof(DestinationSecurityText))]
    private string _destinationSystem = string.Empty;

    /// <summary>ET-127: what the typed destination resolves to locally, straight off the SDE — no ESI call needed
    /// for a name that already carries an id and a security status. Null while nothing matches; never a guess.</summary>
    public SdeSolarSystem? DestinationResolvedSystem =>
        string.IsNullOrWhiteSpace(DestinationSystem) ? null : _sde.FindSolarSystemByName(DestinationSystem.Trim());

    public string? DestinationSecurityText =>
        DestinationResolvedSystem is { } system
            ? $"{OpsecText.Mark(system.SecurityStatus.ToString("0.0", CultureInfo.InvariantCulture))} security"
            : null;

    /// <summary>Typed as-is from the Agency window — see the type docstring for why this never starts pre-filled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _remainingTimeText = string.Empty;

    /// <summary>Set once <see cref="RegisterCommand"/> commits — null while the dialog is still open or was
    /// cancelled.</summary>
    public EscalationRegistration? Result { get; private set; }

    /// <summary>Raised on Register (true) or Cancel (false) — the dialog's cue to close.</summary>
    public event Action<bool>? CloseRequested;

    private bool CanRegister => _ResolvedSite() is not null && _ParseRemaining() is not null;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private void Register()
    {
        if (_ResolvedSite() is not { } site || _ParseRemaining() is not { } remaining)
            return;

        Result = new EscalationRegistration(
            site.Name,
            site.DungeonId,
            DestinationSystem.Trim(),
            DestinationResolvedSystem?.SolarSystemId,
            DateTime.UtcNow + remaining);
        CloseRequested?.Invoke(true);
    }

    /// <summary>
    /// The escalation site Register carries — never free text (ET-451). The pick from <see cref="SiteResults"/> wins
    /// when there is one (ET-125). Otherwise a typed name counts only when, among Escalation sites, it resolves to
    /// exactly one site after canonicalising twins (ET-126 AC-1) — never a guess among genuinely different sites
    /// (AC-2), and never a site of another archetype sharing the name.
    /// </summary>
    private SdeSite? _ResolvedSite() =>
        SelectedSite
        ?? (SdeSiteCanonicalization.Canonicalize(
                [.. CatalogMatches.Where(site => site.ArchetypeName == EscalationArchetypeName)]) is [{ } only]
            ? only
            : null);

    private IReadOnlyList<SdeSite> _EscalationSites(string? query) =>
        [.. _sde.SearchSites(query).Where(site => site.ArchetypeName == EscalationArchetypeName)];

    private int _HistoryRank(SdeSite site) =>
        _previouslyRegistered.IndexOf(site.DungeonId) is >= 0 and var rank ? rank : int.MaxValue;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private TimeSpan? _ParseRemaining() =>
        TimeSpan.TryParse(RemainingTimeText.Trim(), CultureInfo.InvariantCulture, out TimeSpan parsed)
        && parsed > TimeSpan.Zero
            ? parsed
            : null;

    private const string EscalationArchetypeName = "Escalation";
}

/// <summary>What the dialog produced: the name as typed or picked, the catalogue id when a pick made one available
/// (ET-125 AC-2 — never re-derived from the name), the destination system as typed, its solarSystemId when the SDE
/// recognises the name (ET-127 — null is not an error, AC-2), and the computed deadline.</summary>
public sealed record EscalationRegistration(
    string SiteName, int? DungeonId, string DestinationSystem, int? DestinationSolarSystemId, DateTime ExpiresAtUtc);
