using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One source tab in the compositions library: the Local library, or a single coupled server (mirroring the
/// fit browser's Local + tab-per-server). Loads its own rows lazily on first selection through the injected
/// loader, carries its own search-filtered view and a status line (e.g. "not connected"), and serialises reloads so a
/// rapid re-select or a post-mutation refresh never overlaps another sweep on the bound collection.
/// </summary>
public sealed partial class CompositionTabViewModel : ObservableObject
{
    private readonly Func<CompositionTabViewModel, Task> _loader;
    private Task? _loadTask;
    private Task? _queuedReload;
    private string _filter = "";

    public CompositionTabViewModel(string title, bool isLocal, string? serverAddress, Func<CompositionTabViewModel, Task> loader)
    {
        Title = title;
        IsLocal = isLocal;
        ServerAddress = serverAddress;
        _loader = loader;
    }

    public string Title { get; }
    public bool IsLocal { get; }

    /// <summary>The coupled server this tab loads from, or null for the Local library tab.</summary>
    public string? ServerAddress { get; }

    /// <summary>Every loaded row (the master set); <see cref="Compositions"/> is the search-filtered view bound to the UI.</summary>
    public List<CompositionRowViewModel> Loaded { get; } = [];
    public ObservableCollection<CompositionRowViewModel> Compositions { get; } = [];

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Loads once (lazy, on first selection); a repeat call returns the same task.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= _loader(this);

    /// <summary>Forces a reload, serialised after any in-flight load so the bound list never overlaps a sweep. A
    /// reload still waiting behind one is shared, so a burst of changes costs one sweep, not one each.</summary>
    public Task ReloadAsync()
    {
        if (_queuedReload is not null)
            return _queuedReload;

        var previous = _loadTask;
        var reload = _ChainAsync(previous);
        _loadTask = reload;
        if (previous is { IsCompleted: false })
            _queuedReload = reload;
        return reload;
    }

    /// <summary>Whether a change to a composition belongs to this tab's source: the client-only library for the Local
    /// tab, the server the change came from for a server tab.</summary>
    public bool Shows(CompositionChangedEvent change) => IsLocal
        ? change.Data.IsClientOnly
        : !change.Data.IsClientOnly && change.SourceServerAddress == ServerAddress;

    /// <summary>Reloads after a composition change — unless the tab was never opened, which reads fresh on first selection anyway.</summary>
    public Task RefreshAfterChangeAsync() => _loadTask is null ? Task.CompletedTask : ReloadAsync();

    private async Task _ChainAsync(Task? previous)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch { /* a previous sweep's failure must not block the next */ }
            _queuedReload = null;
        }
        await _loader(this);
    }

    /// <summary>Sets the search filter and re-projects <see cref="Loaded"/> into <see cref="Compositions"/>.</summary>
    public void SetFilter(string filter)
    {
        _filter = filter.Trim();
        Compositions.Clear();
        foreach (var row in Loaded.Where(r => _filter.Length == 0 || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
            Compositions.Add(row);
        IsEmpty = Compositions.Count == 0;
    }
}
