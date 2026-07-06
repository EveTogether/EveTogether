using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One tab in the fittings panel. The first tab is always "Local" (the local library, shared
/// with <see cref="MainWindowViewModel.Fittings"/>); each coupled server gets its own tab listing that
/// server's shared fits, loaded lazily on first selection.
/// </summary>
public partial class FittingsTabViewModel : ObservableObject
{
    public string Header { get; }
    public bool IsLocal { get; }
    public string? ServerAddress { get; }

    /// <summary>Local library (only populated for the Local tab; shares the main VM's collection).</summary>
    public ObservableCollection<FittingViewModel> LocalFits { get; }

    /// <summary>Shared fits for this server (only populated for server tabs, lazily).</summary>
    public ObservableCollection<ServerFitRowViewModel> ServerFits { get; } = [];

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isLoaded;

    private readonly Func<FittingsTabViewModel, Task>? _loader;

    /// <summary>Local tab — shares the main library collection; nothing to load.</summary>
    public FittingsTabViewModel(string header, ObservableCollection<FittingViewModel> localFits)
    {
        Header = header;
        IsLocal = true;
        LocalFits = localFits;
        IsLoaded = true;
    }

    /// <summary>Server tab — fits are fetched lazily via <paramref name="loader"/> on first selection.</summary>
    public FittingsTabViewModel(string header, string serverAddress, Func<FittingsTabViewModel, Task> loader)
    {
        Header = header;
        IsLocal = false;
        ServerAddress = serverAddress;
        LocalFits = [];
        _loader = loader;
    }

    /// <summary>Loads the server's shared fits the first time the tab is shown.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (IsLoaded || IsLocal || _loader is null) return;
        IsLoaded = true; // set first so a slow load isn't started twice on rapid re-selection
        await _loader(this);
    }
}
