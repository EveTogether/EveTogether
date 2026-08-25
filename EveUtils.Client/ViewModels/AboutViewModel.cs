using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Esi;
using EveUtils.Client.Imaging;
using EveUtils.Client.Updates;
using EveUtils.Shared.App;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.ViewModels;

/// <summary>An inspiration credit: a project name and the URL it links to.</summary>
public sealed record InspirationLink(string Name, string Url);

/// <summary>
/// Backs the About window: the app identity + version, the creators (with hex ESI portraits), the
/// projects we drew inspiration from, the AGPLv3 license and the mandatory CCP attribution disclaimer. Also the one
/// place that explains how this copy updates, since that answer differs per copy rather than per release.
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    // The creators' EVE character ids — they drive both the name and portrait resolve, so name + face always match.
    private const int RaymondKrahCharacterId = 883434905;
    private const int JithranCharacterId = 90250177;

    public string AppName => "EVE Together";
    public string Version { get; }
    public string Tagline => "A local-first, open-source tooling suite for EVE Online.";

    public string RepositoryUrl => "https://github.com/EveTogether/EveTogether";
    public string LicenseLabel => "Licensed under the GNU Affero General Public License v3.";
    public string LicenseUrl => "https://www.gnu.org/licenses/agpl-3.0.html";
    public string Copyright => $"© {DateTime.UtcNow.Year} RaymondKrah & Jithran";

    // Verbatim CCP disclaimer (Notes.md §Legal) — required wherever EVE/CCP material is shown.
    public string Disclaimer =>
        "Material related to EVE-Online is used with limited permission of CCP Games hf by using official Toolkit. " +
        "No official affiliation or endorsement by CCP Games hf is stated or implied.";

    public ObservableCollection<CreatorRowViewModel> Creators { get; }
    public ObservableCollection<InspirationLink> Inspirations { get; }

    // The updates block. Its wording is driven by UpdateNotice.Classify, so which of the states below shows is
    // decided by the check's message code and never by its text.
    private readonly IUpdateService? _updates;
    private readonly Func<AppRelease, Task>? _onInstallRequested;
    private AppRelease? _offered;

    /// <summary>Raised when the user starts an install from here, so the window closes before the offer opens.</summary>
    public event Action? CloseRequested;

    [ObservableProperty] private string _updateHeadline = "";
    [ObservableProperty] private string _updateDetail = "Updates are downloaded from the project's GitHub releases.";
    [ObservableProperty] private bool _isCheckingForUpdates;

    /// <summary>False for a copy the installer never placed: a button that can never do anything is not a button.</summary>
    [ObservableProperty] private bool _canCheckForUpdates;

    [ObservableProperty] private string _checkForUpdatesLabel = "Check for updates";
    [ObservableProperty] private bool _showReleasesLink;
    [ObservableProperty] private bool _canInstallUpdate;

    public string ReleasesUrl => $"{RepositoryUrl}/releases";

    public AboutViewModel() : this(null, null) { }

    public AboutViewModel(
        ICharacterPortraitProvider? portraits,
        ICharacterInfoService? characterInfo,
        IUpdateService? updates = null,
        IUpdateSupportProbe? updateSupport = null,
        Func<AppRelease, Task>? onInstallRequested = null)
    {
        Version = $"v{AppInfo.Version}";
        _updates = updates;
        _onInstallRequested = onInstallRequested;
        ApplySupport(updateSupport?.Detect() ?? UpdateSupport.NotInstalled);

        // Shuffled per view so no creator is permanently listed first — neither is "the" lead.
        CreatorRowViewModel[] creators =
        [
            new("RaymondKrah", "Creator", "https://github.com/RaymondKrah", RaymondKrahCharacterId),
            new("Jithran", "Creator", "https://github.com/Jithran", JithranCharacterId)
        ];
        Random.Shared.Shuffle(creators);
        Creators = [.. creators];

        Inspirations =
        [
            new InspirationLink("eveship.fit", "https://eveship.fit/"),
            new InspirationLink("pyfa", "https://github.com/pyfa-org/Pyfa"),
            new InspirationLink("EVE Workbench", "https://www.eveworkbench.com/")
        ];

        if (portraits is not null)
            foreach (var creator in Creators)
                _ = creator.LoadAsync(portraits, characterInfo);
    }

    // An unpacked zip, a tarball, an AppImage or a checkout: the ordinary state of everyone who has not yet moved to
    // the installer, so it reads as how this copy works rather than as something being wrong.
    private void ApplySupport(UpdateSupport support)
    {
        if (support is UpdateSupport.NotInstalled)
        {
            UpdateHeadline = "This copy updates manually.";
            UpdateDetail =
                "You're running EVE Together from a folder you unpacked yourself (a .zip, a tarball, an AppImage or a " +
                "source checkout), so it can't replace itself. Grab the installer from the releases page once and " +
                "future versions will update on their own.";
            ShowReleasesLink = true;

            return;
        }

        CanCheckForUpdates = true;
    }

    /// <summary>Asks the feed directly, whatever the startup setting says — the user asked, so they get an answer.</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (_updates is null || IsCheckingForUpdates) return;

        IsCheckingForUpdates = true;
        UpdateHeadline = "Checking for updates…";
        UpdateDetail = "";
        ShowReleasesLink = false;
        CanInstallUpdate = false;

        try
        {
            ApplyCheck(await _updates.CheckAsync());
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void ApplyCheck(Result<AppRelease?> check)
    {
        _offered = check.Value;

        switch (UpdateNotice.Classify(check))
        {
            case UpdateNoticeKind.Available:
                UpdateHeadline = $"EVE Together v{check.Value!.Version} is available.";
                UpdateDetail = $"You're on {Version}. The download is {UpdateDownloadSize.Format(check.Value.SizeBytes)}.";
                CanInstallUpdate = _onInstallRequested is not null;
                CheckForUpdatesLabel = "Check again";
                break;

            case UpdateNoticeKind.UpToDate:
                UpdateHeadline = $"✓ You're on the latest version ({Version}).";
                UpdateDetail = "Checked just now against the project's GitHub releases.";
                CheckForUpdatesLabel = "Check for updates";
                break;

            case UpdateNoticeKind.NotInstalled:
                ApplySupport(UpdateSupport.NotInstalled);
                CanCheckForUpdates = false;
                break;

            // Never "you're up to date": nothing was reachable, so nothing is known about newer builds.
            default:
                UpdateHeadline = "Couldn't check for updates.";
                UpdateDetail = $"{UpdateNotice.Reason(check)} This says nothing about whether a newer version exists — " +
                    "it only means we couldn't ask. Try again when you're back online.";
                ShowReleasesLink = true;
                CheckForUpdatesLabel = "Try again";
                break;
        }
    }

    // Hands the offer back to the main window, which owns the download, the status line and the restart banner —
    // one install path whether the offer arrived at startup or from this button.
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_offered is null || _onInstallRequested is null) return;

        CloseRequested?.Invoke();
        await _onInstallRequested(_offered);
    }
}
