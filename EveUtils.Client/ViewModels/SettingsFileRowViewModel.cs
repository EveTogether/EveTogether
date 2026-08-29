using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.EveSettings;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One character or account row in the settings-sync screen: the name to show, when EVE last wrote the file (which
/// is how you recognise the profile you actually played on) and whether it is picked as a target. A row is never
/// both source and target — selecting it as the source clears and locks its tick.
/// </summary>
public partial class SettingsFileRowViewModel : ViewModelBase
{
    public SettingsFileRowViewModel(
        EveSettingsFile file,
        string displayName,
        IReadOnlyList<string> accountCharacters,
        AccountLinkOrigin? linkOrigin = null)
    {
        File = file;
        _displayName = displayName;
        _accountCharacters = accountCharacters;
        _linkOrigin = linkOrigin;
    }

    public EveSettingsFile File { get; }

    public long Id => File.Id;

    public SettingsFileKind Kind => File.Kind;

    /// <summary>The characters on this account, by name (ET-64) — what makes an account with no name yet
    /// recognisable at all. Empty for characters, and for accounts nothing could be established about.</summary>
    [ObservableProperty] private IReadOnlyList<string> _accountCharacters;

    /// <summary>Whether that list was worked out from EVE's write times or stated by the user — an inference and a
    /// fact are not the same thing, and the row says which it is.</summary>
    [ObservableProperty] private AccountLinkOrigin? _linkOrigin;

    [ObservableProperty] private string _displayName;

    [ObservableProperty] private bool _isTarget;

    [ObservableProperty] private bool _isSource;

    /// <summary>True for an account still showing the placeholder name — the row then offers "name this account".</summary>
    [ObservableProperty] private bool _needsName;

    public bool CanBeTarget => !IsSource;

    /// <summary>The id beside the name: subordinate reference, not something to read instead of the name — it is
    /// how you check you have the file you think you have (and for an account it is all EVE itself offers).</summary>
    public string IdDisplay => File.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>False when the name is the "Character &lt;id&gt;" fallback, which already ends in the id — printing it
    /// twice on one row is noise, not reference.</summary>
    public bool ShowId => !DisplayName.EndsWith(IdDisplay, StringComparison.Ordinal);

    public string LastModifiedDisplay =>
        File.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string SizeDisplay => File.SizeBytes < 1024
        ? $"{File.SizeBytes} B"
        : $"{File.SizeBytes / 1024d:0} KB";

    /// <summary>The account's characters on one line, with where the answer came from — "derived" and "set by you"
    /// are shown apart on purpose, because the user overwrites files on the strength of this.</summary>
    public string HintDisplay => AccountCharacters.Count == 0
        ? string.Empty
        : string.Join(", ", AccountCharacters.Take(3)) +
          (AccountCharacters.Count > 3 ? $" +{AccountCharacters.Count - 3}" : string.Empty) +
          (LinkOrigin == AccountLinkOrigin.UserSet ? " · set by you" : " · from EVE's write times");

    public string HintTooltip => AccountCharacters.Count == 0
        ? string.Empty
        : (LinkOrigin == AccountLinkOrigin.UserSet
              ? "You said this account holds: "
              : "EVE wrote these characters' settings in the same moment as this account's, so they are on it: ") +
          string.Join(", ", AccountCharacters);

    public bool HasHint => AccountCharacters.Count > 0;

    /// <summary>What an account row says when nothing could be established — an invitation to say so yourself,
    /// rather than a blank that looks like an oversight.</summary>
    public bool NeedsLink => Kind == SettingsFileKind.Account && AccountCharacters.Count == 0;

    partial void OnAccountCharactersChanged(IReadOnlyList<string> value) => _NotifyHint();

    partial void OnLinkOriginChanged(AccountLinkOrigin? value) => _NotifyHint();

    private void _NotifyHint()
    {
        OnPropertyChanged(nameof(HintDisplay));
        OnPropertyChanged(nameof(HintTooltip));
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(NeedsLink));
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(ShowId));

    partial void OnIsSourceChanged(bool value)
    {
        if (value)
            IsTarget = false;
        OnPropertyChanged(nameof(CanBeTarget));
    }
}
