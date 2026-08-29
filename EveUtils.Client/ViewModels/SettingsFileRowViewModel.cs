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
    public SettingsFileRowViewModel(EveSettingsFile file, string displayName, IReadOnlyList<string> accountHint)
    {
        File = file;
        _displayName = displayName;
        AccountHint = accountHint;
    }

    public EveSettingsFile File { get; }

    public long Id => File.Id;

    public SettingsFileKind Kind => File.Kind;

    /// <summary>Characters last written in the same session as this account — the only recognisable thing EVE
    /// leaves behind about an account. Empty for characters and for accounts we could not tell apart.</summary>
    public IReadOnlyList<string> AccountHint { get; }

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

    public string HintDisplay => AccountHint.Count == 0
        ? string.Empty
        : "last saved with " + string.Join(", ", AccountHint.Take(3)) +
          (AccountHint.Count > 3 ? $" +{AccountHint.Count - 3}" : string.Empty);

    public bool HasHint => AccountHint.Count > 0;

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(ShowId));

    partial void OnIsSourceChanged(bool value)
    {
        if (value)
            IsTarget = false;
        OnPropertyChanged(nameof(CanBeTarget));
    }
}
