using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// Avalonia implementation of <see cref="IDialogService"/>. The owner window is set once from
/// <c>App.OnFrameworkInitializationCompleted</c> after the main window is created. Called from
/// view-model commands, which already run on the UI thread.
/// </summary>
public sealed class DialogService : IDialogService, ISingletonService
{
    private Window? _owner;
    private readonly ModuleHostService _moduleHost = new();
    private readonly Dictionary<string, DpsOverlayWindow> _dpsOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Window> _infoPopouts = new(); // non-modal type-info cards, shown ownerless

    public void SetOwner(Window owner)
    {
        _owner = owner;
        _moduleHost.SetOwner(owner);
    }

    /// <summary>Wires the docked module host (the main view-model): the tab sink for hosted modules.</summary>
    public void SetHost(IModuleHostDisplay host) => _moduleHost.SetHost(host);

    /// <summary>Re-render the open module set after a dock/float switch — migrates modules to the new mode (no orphans).</summary>
    public void SwitchMode() => _moduleHost.SwitchMode();

    // Opens a non-modal feature window as a module: a docked tab, or a floating window — handled by the host.
    private void Route(Window window, string title, string? moduleKey = null, string? moduleId = null) =>
        _moduleHost.Open(window, title, moduleKey, moduleId);

    public void ShowDpsOverlay(DpsViewModel tracker)
    {
        if (_owner is null) return;

        // One overlay per character: re-opening focuses the existing window instead of stacking duplicates.
        if (_dpsOverlays.TryGetValue(tracker.Character, out var existing))
        {
            existing.Activate();
            return;
        }

        var overlay = new DpsOverlayWindow(tracker);
        _dpsOverlays[tracker.Character] = overlay;
        overlay.Closed += (_, _) => _dpsOverlays.Remove(tracker.Character);
        // Shown ownerless so the overlay is independent of the main window (minimizing the main window no longer
        // minimizes the overlay). The main window's close handler closes any open overlays explicitly.
        overlay.Show();
    }

    /// <summary>Open pop-out windows independent of the main window: floating modules + DPS overlays + info cards.
    /// Used by the main window's close handler to decide whether to confirm before quitting.</summary>
    public int OpenPopoutCount => _dpsOverlays.Count + _moduleHost.FloatingWindowCount + _infoPopouts.Count;

    /// <summary>Close every open pop-out window — called when the main window is closing so leftover ownerless
    /// windows don't keep the app alive.</summary>
    public void CloseAllPopouts()
    {
        foreach (var overlay in _dpsOverlays.Values.ToList())
            overlay.Close();
        foreach (var info in _infoPopouts.ToList())
            info.Close();
        _moduleHost.CloseFloatingWindows();
    }

    /// <summary>Confirm dialog with an extra "don't ask again" opt-out checkbox. Returns whether the
    /// user confirmed and whether they ticked the opt-out.</summary>
    public async Task<(bool Confirmed, bool OptOut)> ConfirmWithOptOutAsync(string title, string message, string okText, string optOutText)
    {
        if (_owner is null) return (false, false);
        var dialog = new MessageBoxWindow(title, message, confirm: true, okText: okText, optOutText: optOutText);
        var confirmed = await dialog.ShowDialog<bool>(_owner);
        return (confirmed, dialog.OptOutChecked);
    }

    public async Task<IReadOnlyList<string>?> SelectScopesAsync(IReadOnlyList<EsiScopeRequirement> available,
        IReadOnlyCollection<string>? preselected = null)
    {
        if (_owner is null) return null;
        var dialog = new ScopeSelectionWindow(available, preselected);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(_owner);
    }

    public async Task<IReadOnlyList<int>?> SelectFittingsAsync(IReadOnlyList<EsiFitting> fits)
    {
        if (_owner is null) return null;
        var dialog = new FitImportWindow(fits);
        return await dialog.ShowDialog<IReadOnlyList<int>?>(_owner);
    }

    public async Task<int?> PickCharacterAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new CharacterPickerWindow(prompt, options);
        return await dialog.ShowDialog<int?>(_owner);
    }

    public async Task<IReadOnlyList<int>?> PickCharactersAsync(string prompt, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new CharacterPickerWindow(prompt, options, multiSelect: true);
        return await dialog.ShowDialog<IReadOnlyList<int>?>(_owner);
    }

    public async Task<CoupleServerResult?> CoupleServerAsync(Func<string, CancellationToken, Task<string?>> probeServerName)
    {
        if (_owner is null) return null;
        var dialog = new CoupleServerWindow(probeServerName);
        return await dialog.ShowDialog<CoupleServerResult?>(_owner);
    }

    public async Task<string?> SelectServerAsync(string prompt, IReadOnlyList<ServerPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new ServerPickerWindow(prompt, options);
        return await dialog.ShowDialog<string?>(_owner);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (_owner is null) return;
        var dialog = new MessageBoxWindow(title, message);
        await dialog.ShowDialog(_owner);
    }

    public async Task<string?> ImportFitTextAsync()
    {
        if (_owner is null) return null;
        return await new FitTextImportWindow().ShowDialog<string?>(_owner);
    }

    public async Task<string?> ImportFitEsfLinkAsync()
    {
        if (_owner is null) return null;
        return await new FitEsfImportWindow().ShowDialog<string?>(_owner);
    }

    public async Task<FitMetadataDraft?> EditFitMetadataAsync(FitMetadataDraft current)
    {
        if (_owner is null) return null;
        return await new FitMetadataDialog(current).ShowDialog<FitMetadataDraft?>(_owner);
    }

    public async Task ShowFitExportAsync(string fitName, string eft, string dna, string eveshipUrl)
    {
        if (_owner is null) return;
        await new FitExportWindow(fitName, eft, dna, eveshipUrl).ShowDialog(_owner);
    }

    public async Task SetClipboardTextAsync(string text)
    {
        var clipboard = _owner?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "Delete")
    {
        if (_owner is null) return false;
        var dialog = new MessageBoxWindow(title, message, confirm: true, okText: okText);
        return await dialog.ShowDialog<bool>(_owner);
    }

    public async Task ShowCharacterAsync(CharacterDialogViewModel viewModel)
    {
        if (_owner is null) return;
        var dialog = new CharacterWindow(viewModel);
        await dialog.ShowDialog(_owner);
    }

    public async Task<bool> ShowServerTrustAsync(string displayName, string address, string fingerprint, string statusLabel)
    {
        if (_owner is null) return false;
        var dialog = new ServerTrustWindow(displayName, address, fingerprint, statusLabel);
        return await dialog.ShowDialog<bool>(_owner);
    }

    public void ShowFleets(FleetsViewModel viewModel) =>
        Route(new FleetsWindow(viewModel), "FLEETS", "fleet");

    public void ShowSettings(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, Theming.FactionTheme currentFaction, string sdeVersionLabel, Func<SettingsResult, Task> onApply, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", LocalApi.ILocalApiServer? localApiServer = null)
    {
        var window = new SettingsWindow(currentDirectory, detectedDefault, shareLocation, shareBounty, shareCombat, loadTypeImages, currentFaction, sdeVersionLabel, openFitDetailAfterImport, toastPosition, enableLocalApi, localApiPort, localApiStatusLabel, localApiServer, onApply);
        Route(window, "SETTINGS", "settings"); // docked tab in docked mode, floating window otherwise
    }

    public async Task<bool> ShowFleetSharingAsync(FleetShareViewModel viewModel)
    {
        if (_owner is null) return false;
        return await new FleetShareWindow(viewModel).ShowDialog<bool>(_owner);
    }

    public void ShowMetrics(MetricsWindowViewModel viewModel) =>
        Route(new MetricsWindow(viewModel), "METRICS");

    public async Task ShowAboutAsync(AboutViewModel viewModel)
    {
        if (_owner is null) return;
        await new AboutWindow(viewModel).ShowDialog(_owner);
    }

    public async Task<Fleet.FleetEditResult?> EditFleetAsync(Fleet.FleetInfo? existing)
    {
        if (_owner is null) return null;
        var dialog = existing is null ? new FleetEditWindow() : new FleetEditWindow(existing);
        return await dialog.ShowDialog<Fleet.FleetEditResult?>(_owner);
    }

    // The composition editor opens as a hosted module (docked tab when docked, floating window when floating) rather
    // than a modal dialog, so it sits alongside the library like the other feature modules. The Task still resolves
    // with whether the composition was saved (so the library reloads) — completed when the editor closes by any path.
    public Task<bool> ShowCompositionEditorAsync(CompositionEditorViewModel viewModel)
    {
        if (_owner is null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        // Save/Cancel raise CloseRequested with the saved flag — resolve straight from it (independent of how the
        // host then closes the window). Closing the tab/window by its X never raises it, so resolve false there too.
        viewModel.CloseRequested += result => tcs.TrySetResult(result);
        var window = new CompositionEditorWindow(viewModel);
        window.Closed += (_, _) => tcs.TrySetResult(false);

        var title = string.IsNullOrWhiteSpace(viewModel.Name) ? viewModel.Title : viewModel.Name;
        Route(window, title, "compositions");
        return tcs.Task;
    }

    public async Task<IReadOnlyList<Fleet.FitReferenceInfo>?> ShowFitPickerAsync(FitPickerViewModel viewModel)
    {
        if (_owner is null) return null;
        return await new FitPickerWindow(viewModel).ShowDialog<IReadOnlyList<Fleet.FitReferenceInfo>?>(_owner);
    }

    public async Task<Fleet.FitReferenceInfo?> PickFitAsync(FitPickerViewModel viewModel)
    {
        if (_owner is null) return null;
        return await new FitPickerWindow(viewModel).ShowDialog<Fleet.FitReferenceInfo?>(_owner);
    }

    public void ShowInbox(InboxViewModel viewModel)
    {
        _ = viewModel.OnOpenedAsync();   // mark shown messages read so the unread badge clears
        Route(new InboxWindow(viewModel), "INBOX", "inbox");
    }

    public void ShowLogs(ClientLogViewModel viewModel) =>
        Route(new LogsWindow(viewModel), "APP LOGS", "logs");

    public void ShowEsiMetrics(EsiMetricsViewModel viewModel) =>
        Route(new EsiMetricsWindow(viewModel), "ESI METRICS", "esi");

    public void ShowFitBrowser(FitBrowserViewModel viewModel) =>
        Route(new FitBrowserWindow(viewModel), "FIT BROWSER", "fits");

    public void ShowCompositions(CompositionsViewModel viewModel) =>
        Route(new CompositionsWindow(viewModel), "COMPOSITIONS", "compositions");

    public void ShowFitDetail(FitDetailWindowViewModel viewModel) =>
        Route(new FitDetailWindow(viewModel), string.IsNullOrWhiteSpace(viewModel.Name) ? "FIT DETAIL" : viewModel.Name, "fits");

    public void ShowTypeInfo(TypeInfoWindowViewModel viewModel)
    {
        if (_owner is null) return;
        // Non-modal info card, shown ownerless so it survives a main-window minimize; tracked so the close
        // handler can tear it down with the rest of the pop-outs.
        var window = new TypeInfoWindow(viewModel);
        _infoPopouts.Add(window);
        window.Closed += (_, _) => _infoPopouts.Remove(window);
        window.Show();
    }

    public async Task<FleetInviteResult?> PickFleetInviteAsync(string fleetName, IReadOnlyList<CharacterPickOption> options)
    {
        if (_owner is null) return null;
        var dialog = new FleetInviteWindow(fleetName, options);
        return await dialog.ShowDialog<FleetInviteResult?>(_owner);
    }

    public async Task<int?> AddExternalMemberAsync(Fleet.IExternalCharacterLookup lookup)
    {
        if (_owner is null) return null;
        var dialog = new AddExternalMemberWindow(lookup);
        return await dialog.ShowDialog<int?>(_owner);
    }

    public async Task<string?> PromptTextAsync(string title, string header, string? defaultValue = null)
    {
        if (_owner is null) return null;
        var dialog = new TextPromptWindow(title, header, defaultValue);
        return await dialog.ShowDialog<string?>(_owner);
    }

    public async Task<bool> ConfirmStartFleetAsync(string fleetName, int unlinkedCount)
    {
        if (_owner is null) return false;
        var dialog = new StartFleetEsiPromptWindow(fleetName, unlinkedCount);
        return await dialog.ShowDialog<bool>(_owner);
    }

    public void ShowRoster(FleetRosterViewModel viewModel) =>
        // One roster module per fleet (de-duped on the fleet id): MANAGE on a second fleet opens its own window
        // instead of re-selecting the first fleet's roster, which used to stay bound to the original fleet.
        Route(new FleetRosterWindow(viewModel), $"FLEET ROSTER · {viewModel.FleetName}", "fleet",
            moduleId: $"fleet-roster:{viewModel.FleetId}");

    public void ShowFleetMetrics(FleetMetricsViewModel viewModel) =>
        Route(new FleetMetricsWindow(viewModel), "FLEET METRICS", "fleet");

    public async Task ShowSdeUpdateAsync(SdeProgressViewModel viewModel)
    {
        if (_owner is null) return;
        // Modal: blocks interaction while the static-data store is (re)built; the window closes itself on success.
        await new SdeProgressWindow(viewModel).ShowDialog(_owner);
    }
}
