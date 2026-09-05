using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The escalation dialog (ET-125): a site (searched the same way <see cref="ManualRunStartViewModel"/>
/// searches the site catalogue — search field plus result list, no <c>AutoCompleteBox</c>), a destination system as
/// free text, and the remaining time the Agency showed, turned into a deadline. No field here ever carries a
/// default: ET-124 measured one escalation at 23h57m45s remaining, which does not prove every escalation carries a
/// 24-hour window, so the pilot always types what the Agency actually showed (AC-3).</summary>
public sealed partial class EscalationDialogViewModel : ObservableObject
{
    private readonly ISdeAccessor _sde;

    public EscalationDialogViewModel(ISdeAccessor sde)
    {
        _sde = sde;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteResults))]
    [NotifyPropertyChangedFor(nameof(HasSiteResults))]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _siteQuery = string.Empty;

    /// <summary>Escalation sites matching what is typed so far. Narrowed to the Escalation archetype in code —
    /// not trusted to <see cref="ISdeAccessor.SearchSites"/>'s own archetype filter, which a test double is free to
    /// ignore (it only has to match the interface, not every optional narrowing).</summary>
    public IReadOnlyList<SdeSite> SiteResults => string.IsNullOrWhiteSpace(SiteQuery)
        ? []
        : [.. _sde.SearchSites(SiteQuery).Where(site => site.ArchetypeName == EscalationArchetypeName)];

    public bool HasSiteResults => SiteResults.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSite))]
    private SdeSite? _selectedSite;

    public bool HasSelectedSite => SelectedSite is not null;

    [ObservableProperty] private string _destinationSystem = string.Empty;

    /// <summary>Typed as-is from the Agency window — see the type docstring for why this never starts pre-filled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _remainingTimeText = string.Empty;

    /// <summary>Set once <see cref="RegisterCommand"/> commits — null while the dialog is still open or was
    /// cancelled.</summary>
    public EscalationRegistration? Result { get; private set; }

    /// <summary>Raised on Register (true) or Cancel (false) — the dialog's cue to close.</summary>
    public event Action<bool>? CloseRequested;

    private bool CanRegister => !string.IsNullOrWhiteSpace(SiteQuery) && _ParseRemaining() is not null;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private void Register()
    {
        if (_ParseRemaining() is not { } remaining)
            return;

        Result = new EscalationRegistration(
            SelectedSite?.Name ?? SiteQuery.Trim(),
            SelectedSite?.DungeonId,
            DestinationSystem.Trim(),
            DateTime.UtcNow + remaining);
        CloseRequested?.Invoke(true);
    }

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
/// (ET-125 AC-2 — never re-derived from the name), the destination system as typed, and the computed deadline.</summary>
public sealed record EscalationRegistration(string SiteName, int? DungeonId, string DestinationSystem, DateTime ExpiresAtUtc);
