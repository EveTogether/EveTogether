using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Setup;

/// <summary>A known server as a one-click choice on the server step.</summary>
public sealed partial class KnownServerOption(KnownServer server, Action<KnownServerOption> onSelected) : ObservableObject
{
    public KnownServer Server { get; } = server;
    public string CoupledText { get; } = $"Already coupled: {string.Join(", ", server.CoupledCharacterNames)}";

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) onSelected(this);
    }
}
