using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One tile in the SHOW row: All, Kills, Losses or Not linked. Single-select, unlike a RUNS filter tile —
/// picking one clears the others rather than excluding it from what stays on.</summary>
public sealed partial class KillmailShowFilterTileViewModel(KillmailShowFilter key, string name, Action<KillmailShowFilter> select)
    : ObservableObject
{
    public KillmailShowFilter Key { get; } = key;

    public string Name { get; } = name;

    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isOn;

    [RelayCommand]
    private void Select() => select(Key);
}
