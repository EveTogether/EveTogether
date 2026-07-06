using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Logging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// The client log window: the local counterpart of the server's Blazor <c>/logs</c> page. Shows this
/// client's in-memory <see cref="ILogStore"/> (captured Warning and above entries, newest first), updates live as
/// entries arrive (<see cref="ILogStore.EntryAdded"/>), and offers a Clear. Created once by the main window and
/// shared with the non-modal window, so it keeps updating while open and is never disposed on close.
/// </summary>
public partial class ClientLogViewModel : ViewModelBase, ISingletonService
{
    private readonly ILogStore? _store;
    private readonly IDialogService? _dialogs;

    public ObservableCollection<ClientLogRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    public bool IsEmpty => Count == 0;

    /// <summary>Design-time / fallback (no services).</summary>
    public ClientLogViewModel()
    {
    }

    public ClientLogViewModel(ILogStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        _store.EntryAdded += OnEntryAdded;
        Reload();
    }

    // EntryAdded fires on the logging thread; marshal onto the UI thread before touching the collection.
    private void OnEntryAdded(LogEntry _) => Dispatcher.UIThread.Post(Reload);

    // Put one entry on the clipboard (full timestamp/level/category/message + exception) so an error can be
    // forwarded without scrolling back to find it. No-op in the design-time/fallback ctor (no dialog service).
    [RelayCommand]
    private async Task CopyEntry(ClientLogRowViewModel? row)
    {
        if (row is null || _dialogs is null) return;
        await _dialogs.SetClipboardTextAsync(row.CopyText);
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void Clear()
    {
        _store?.Clear();
        Reload();
    }

    private void Reload()
    {
        Entries.Clear();
        if (_store is not null)
            foreach (var entry in _store.GetAll().OrderByDescending(e => e.Timestamp))
                Entries.Add(new ClientLogRowViewModel(entry));
        Count = Entries.Count;
    }
}
