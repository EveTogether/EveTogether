using System;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless UI verification for the client log window. Seeds this client's in-memory <see cref="ILogStore"/>,
/// drives the real <see cref="ClientLogViewModel"/> the LOGS button opens, and asserts newest-first ordering, the
/// exception flag, the empty state, and that Clear empties both the store and the list. The actual window is rendered
/// headless so the XAML bindings are exercised too.
/// </summary>
public class ClientLogViewTests
{
    [AvaloniaFact]
    public void LogWindow_ShowsStoreEntries_NewestFirst_RendersAndClears()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var store = services.GetRequiredService<ILogStore>();
        store.Clear();

        store.Add(new LogEntry(DateTimeOffset.UtcNow.AddSeconds(-3), LogLevel.Error, "EveUtils.Client.Older", "older error", null));
        store.Add(new LogEntry(DateTimeOffset.UtcNow.AddSeconds(-1), LogLevel.Warning, "EveUtils.Client.Esi", "ESI GET /characters/77/fleet/ returned 404 Not Found. Body: {\"error\":\"Character is not in a fleet\"}", null));
        store.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Critical, "EveUtils.Client.Newer", "newer boom", "System.Exception: boom\n  at X()"));

        var vm = new ClientLogViewModel(store, services.GetRequiredService<IDialogService>());
        Assert.False(vm.IsEmpty);
        Assert.Equal(3, vm.Count);
        Assert.Equal("newer boom", vm.Entries[0].Message);   // newest first
        Assert.True(vm.Entries[0].HasException);
        Assert.Equal("Warning", vm.Entries[1].LevelText);    // a captured non-error, tinted amber not red
        Assert.False(vm.Entries[1].HasException);
        Assert.Equal("older error", vm.Entries[2].Message);
        Assert.False(vm.Entries[2].HasException);

        // Render the real window so the XAML + bindings are validated headless.
        var window = new LogsWindow(vm) { Width = 640, Height = 560 };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save("/tmp/eveutils-client-logs.png");
        window.Close();

        // Clear empties the store + the list, flipping the empty state.
        vm.ClearCommand.Execute(null);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Entries);
        Assert.Empty(store.GetAll());
    }

    [AvaloniaFact]
    public async Task CopyEntry_PutsTheWholeEntryOnTheClipboard()
    {
        var dialog = new RecordingDialogService();
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialog));
        var store = instance.Services.GetRequiredService<ILogStore>();
        store.Clear();
        store.Add(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Error,
            "EveUtils.Client.Esi.SomeVeryLongLoggerCategoryThatGetsTruncatedInTheRow",
            "boom happened", "System.Exception: boom\n  at X()"));

        var vm = new ClientLogViewModel(store, dialog);
        var row = vm.Entries[0];
        await vm.CopyEntryCommand.ExecuteAsync(row);

        // The clipboard gets the row's full plain-text form: message, exception, and the UNtruncated category
        // (the row's visible Category is tail-truncated for display, the copy keeps the whole thing).
        Assert.Equal(row.CopyText, dialog.LastClipboardText);
        Assert.Contains("boom happened", dialog.LastClipboardText);
        Assert.Contains("System.Exception: boom", dialog.LastClipboardText);
        Assert.Contains("EveUtils.Client.Esi.SomeVeryLongLoggerCategoryThatGetsTruncatedInTheRow",
            dialog.LastClipboardText);
        Assert.StartsWith("…", row.Category);   // proves the display value was truncated, the copy was not
    }
}
