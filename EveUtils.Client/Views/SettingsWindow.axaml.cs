using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Input;
using EveUtils.Client.LocalApi;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>
/// App settings, shown as a hostable module: a docked tab in docked mode, a floating window otherwise.
/// A left-hand category list (General / Interface / Privacy / Integrations) switches the visible content panel on
/// the right; each panel groups its settings under sub-headings. Save applies everything at once through the
/// <see cref="_onApply"/> callback (the caller persists + applies live); Cancel/close applies nothing. Control
/// references are cached at construction so the handlers keep working after the module host re-parents the content
/// into a tab (which clears the window's own content).
/// </summary>
public partial class SettingsWindow : ChromedWindow, IHostableModuleWindow
{
    private readonly string _detectedDefault = "";
    private readonly ILocalApiServer? _localApi;
    private readonly ClipboardWatchService? _clipboardWatch;
    private readonly Func<SettingsResult, Task>? _onApply;

    // Cached at construction (the instances survive the module host re-parenting; FindControl on the window would
    // return null once the content is stolen for a docked tab).
    private TextBox _gamelogDirBox = null!;
    private TextBlock _hintBlock = null!;
    private CheckBox _shareLocationBox = null!, _shareBountyBox = null!, _shareCombatBox = null!;
    private CheckBox? _shareLootBox;
    private CheckBox? _shareMiningBox;
    private CheckBox _loadTypeImagesBox = null!, _openFitDetailAfterImportBox = null!, _enableLocalApiBox = null!;
    private CheckBox _openFleetRunWindowBox = null!;
    private CheckBox? _autoPublishFleetRunsBox;
    private CheckBox _checkUpdatesOnStartupBox = null!, _watchClipboardBox = null!;
    private TextBlock _clipboardConsumersBlock = null!, _clipboardUnsupportedBlock = null!;
    private ComboBox _toastPositionBox = null!;
    private TextBox _localApiPortBox = null!;
    private Ellipse _localApiStatusDot = null!;
    private TextBlock _localApiStatusBlock = null!;
    private Button _localApiStartStopButton = null!, _localApiDocsButton = null!, _localApiWidgetButton = null!;
    private RadioButton _factionGallente = null!, _factionAmarr = null!, _factionCaldari = null!, _factionMinmatar = null!;
    private StackPanel _generalPanel = null!, _interfacePanel = null!, _privacyPanel = null!, _integrationsPanel = null!;
    private StackPanel _keyboardShortcutsPanel = null!, _shortcutRowsPanel = null!;
    private TextBlock _shortcutMessageBlock = null!;

    // Keyboard shortcuts (ET-209): each row persists itself the moment it changes, independent of this window's own
    // Save/Cancel — conflicts have to be visible immediately, not deferred to a batch Save the user might cancel.
    private KeyboardShortcutRegistry? _shortcutRegistry;
    private readonly Dictionary<ShortcutAction, Button> _shortcutGestureButtons = new();
    private readonly Dictionary<ShortcutAction, Button> _shortcutResetButtons = new();
    private ShortcutAction? _recordingAction;

    /// <summary>Set by the module host so Save/Cancel dismiss the docked tab; null when floating (then we Close()).</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>
    /// Index of the Privacy &amp; Sharing entry in <c>CategoryNav</c>, for callers that want the window opened there.
    /// </summary>
    /// <remarks>
    /// Lives here rather than at the caller because the order it refers to is in this file's own markup.
    /// </remarks>
    public const int PrivacyCategory = 2;

    /// <summary>Index of the Keyboard shortcuts entry in <c>CategoryNav</c>.</summary>
    public const int KeyboardShortcutsCategory = 4;

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SettingsWindow(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, Theming.FactionTheme currentFaction, string sdeVersionLabel, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", ILocalApiServer? localApiServer = null, bool checkUpdatesOnStartup = true, ClipboardWatchService? clipboardWatch = null, Func<SettingsResult, Task>? onApply = null,
        int initialCategory = 0, bool openFleetRunWindowImmediately = false, bool autoPublishFleetRuns = true, bool shareLoot = false, bool shareMining = false) : this()
    {
        _detectedDefault = detectedDefault;
        _localApi = localApiServer;
        _clipboardWatch = clipboardWatch;
        _onApply = onApply;

        _gamelogDirBox = this.FindControl<TextBox>("GamelogDirBox")!;
        _hintBlock = this.FindControl<TextBlock>("HintBlock")!;
        _shareLocationBox = this.FindControl<CheckBox>("ShareLocationBox")!;
        _shareBountyBox = this.FindControl<CheckBox>("ShareBountyBox")!;
        _shareCombatBox = this.FindControl<CheckBox>("ShareCombatBox")!;
        _loadTypeImagesBox = this.FindControl<CheckBox>("LoadTypeImagesBox")!;
        _openFitDetailAfterImportBox = this.FindControl<CheckBox>("OpenFitDetailAfterImportBox")!;
        _openFleetRunWindowBox = this.FindControl<CheckBox>("OpenFleetRunWindowBox")!;
        _checkUpdatesOnStartupBox = this.FindControl<CheckBox>("CheckUpdatesOnStartupBox")!;
        _watchClipboardBox = this.FindControl<CheckBox>("WatchClipboardBox")!;
        _clipboardConsumersBlock = this.FindControl<TextBlock>("ClipboardConsumersBlock")!;
        _clipboardUnsupportedBlock = this.FindControl<TextBlock>("ClipboardUnsupportedBlock")!;
        _enableLocalApiBox = this.FindControl<CheckBox>("EnableLocalApiBox")!;
        _toastPositionBox = this.FindControl<ComboBox>("ToastPositionBox")!;
        _localApiPortBox = this.FindControl<TextBox>("LocalApiPortBox")!;
        _localApiStatusDot = this.FindControl<Ellipse>("LocalApiStatusDot")!;
        _localApiStatusBlock = this.FindControl<TextBlock>("LocalApiStatusBlock")!;
        _localApiStartStopButton = this.FindControl<Button>("LocalApiStartStopButton")!;
        _localApiDocsButton = this.FindControl<Button>("LocalApiDocsButton")!;
        _localApiWidgetButton = this.FindControl<Button>("LocalApiWidgetButton")!;
        _factionGallente = this.FindControl<RadioButton>("FactionGallente")!;
        _factionAmarr = this.FindControl<RadioButton>("FactionAmarr")!;
        _factionCaldari = this.FindControl<RadioButton>("FactionCaldari")!;
        _factionMinmatar = this.FindControl<RadioButton>("FactionMinmatar")!;
        _generalPanel = this.FindControl<StackPanel>("GeneralPanel")!;
        _interfacePanel = this.FindControl<StackPanel>("InterfacePanel")!;
        _privacyPanel = this.FindControl<StackPanel>("PrivacyPanel")!;
        _integrationsPanel = this.FindControl<StackPanel>("IntegrationsPanel")!;
        _keyboardShortcutsPanel = this.FindControl<StackPanel>("KeyboardShortcutsPanel")!;
        _shortcutRowsPanel = this.FindControl<StackPanel>("ShortcutRowsPanel")!;
        _shortcutMessageBlock = this.FindControl<TextBlock>("ShortcutMessageBlock")!;
        BuildShortcutRows();

        _gamelogDirBox.Text = string.IsNullOrWhiteSpace(currentDirectory) ? detectedDefault : currentDirectory;
        _gamelogDirBox.TextChanged += (_, _) => UpdateHint();

        _shareLocationBox.IsChecked = shareLocation;
        _shareBountyBox.IsChecked = shareBounty;
        _shareLootBox = this.FindControl<CheckBox>("ShareLootBox");
        if (_shareLootBox is not null)
            _shareLootBox.IsChecked = shareLoot;
        _shareMiningBox = this.FindControl<CheckBox>("ShareMiningBox");
        if (_shareMiningBox is not null)
            _shareMiningBox.IsChecked = shareMining;
        _shareCombatBox.IsChecked = shareCombat;
        _loadTypeImagesBox.IsChecked = loadTypeImages;
        _openFitDetailAfterImportBox.IsChecked = openFitDetailAfterImport;
        _openFleetRunWindowBox.IsChecked = openFleetRunWindowImmediately;
        _autoPublishFleetRunsBox = this.FindControl<CheckBox>("AutoPublishFleetRunsBox");
        if (_autoPublishFleetRunsBox is not null)
            _autoPublishFleetRunsBox.IsChecked = autoPublishFleetRuns;
        _checkUpdatesOnStartupBox.IsChecked = checkUpdatesOnStartup;
        this.FindControl<TextBlock>("SdeVersionBlock")!.Text = sdeVersionLabel;
        this.FindControl<TextBlock>("DataFolderBlock")!.Text = Composition.ClientServices.DataDirectory();
        _toastPositionBox.SelectedIndex = (int)toastPosition;
        _enableLocalApiBox.IsChecked = enableLocalApi;
        _localApiPortBox.Text = localApiPort.ToString();

        // With a live server we reflect (and control) its real state; without one (tests/designer) we show the
        // static label passed in and disable the live Start/Stop button.
        if (_localApi is not null)
        {
            _localApi.StatusChanged += OnLocalApiStatusChanged;
            Closed += (_, _) => _localApi.StatusChanged -= OnLocalApiStatusChanged;
            ApplyLocalApiStatus(_localApi.Status);
        }
        else
        {
            _localApiStatusBlock.Text = localApiStatusLabel;
            _localApiStartStopButton.IsEnabled = false;
        }

        ApplyClipboardDisclosure();

        FactionRadioFor(currentFaction).IsChecked = true;
        UpdateHint();

        this.FindControl<ListBox>("CategoryNav")!.SelectedIndex = initialCategory; // fires OnCategoryChanged
    }

    // Switch the visible category panel to match the selected nav item.
    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_generalPanel is null) return; // selection set during XAML load, before caching — ignore

        var index = (sender as ListBox)?.SelectedIndex ?? 0;
        _generalPanel.IsVisible = index == 0;
        _interfacePanel.IsVisible = index == 1;
        _privacyPanel.IsVisible = index == 2;
        _integrationsPanel.IsVisible = index == 3;
        _keyboardShortcutsPanel.IsVisible = index == KeyboardShortcutsCategory;
    }

    private void OnLocalApiStatusChanged(LocalApiStatusSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => ApplyLocalApiStatus(snapshot));

    // Reflect the live server state in the dot, label and button, and keep the enable checkbox in sync so Save
    // persists the same intent the user just toggled live.
    private void ApplyLocalApiStatus(LocalApiStatusSnapshot snapshot)
    {
        var (color, text, button) = snapshot.Status switch
        {
            LocalApiStatus.Running => ("#4EC79E", $"Running on {snapshot.Url}", "Stop"),
            LocalApiStatus.PortInUse => ("#E3B341", snapshot.Message ?? $"Port {snapshot.Port} is in use", "Start"),
            LocalApiStatus.Error => ("#CB4D3E", snapshot.Message ?? "Failed to start", "Start"),
            _ => ("#6B7280", "Stopped", "Start")
        };

        _localApiStatusDot.Fill = SolidColorBrush.Parse(color);
        _localApiStatusBlock.Text = text;
        _localApiStartStopButton.Content = button;
        var running = snapshot.Status == LocalApiStatus.Running;
        _localApiDocsButton.IsEnabled = running;
        _localApiWidgetButton.IsEnabled = running;
        _enableLocalApiBox.IsChecked = running;
    }

    // Opens the interactive Scalar API reference in the browser (only enabled while the server runs).
    private void OnOpenApiDocs(object? sender, RoutedEventArgs e)
    {
        var url = _localApi?.Status.Url;
        if (string.IsNullOrEmpty(url)) return;
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri($"{url}/scalar/"));
    }

    // Opens the ready-to-run sample DPS widget in the browser (only enabled while the server runs).
    private void OnOpenWidget(object? sender, RoutedEventArgs e)
    {
        var url = _localApi?.Status.Url;
        if (string.IsNullOrEmpty(url)) return;
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri($"{url}/widget"));
    }

    // Live start/stop of the local API host without leaving the view. Uses the port currently in the box; the
    // status indicator and checkbox update from the resulting StatusChanged event.
    private async void OnToggleLocalApi(object? sender, RoutedEventArgs e)
    {
        if (_localApi is null) return;

        if (_localApi.Status.Status == LocalApiStatus.Running)
        {
            await _localApi.StopAsync();
            return;
        }

        var port = int.TryParse(_localApiPortBox.Text, out var p) && p is > 0 and <= 65535 ? p : LocalApiServer.DefaultPort;
        await _localApi.ApplyAsync(enabled: true, port);
    }

    // The list of features is read from the watcher's live subscribers rather than written out here, so the
    // disclosure cannot drift away from what is actually listening.
    private void ApplyClipboardDisclosure()
    {
        var supported = _clipboardWatch?.IsSupported ?? false;
        _watchClipboardBox.IsChecked = _clipboardWatch?.IsWatching ?? false;
        _watchClipboardBox.IsEnabled = supported;
        _clipboardUnsupportedBlock.IsVisible = _clipboardWatch is not null && !supported;

        var consumers = _clipboardWatch?.Consumers ?? [];
        _clipboardConsumersBlock.Text = consumers.Count == 0
            ? "Used by: nothing yet. While no feature is listening the clipboard is not read at all, so switching this on changes nothing until one does."
            : $"Used by: {string.Join(", ", consumers)}.";
    }

    private RadioButton FactionRadioFor(Theming.FactionTheme faction) => faction switch
    {
        Theming.FactionTheme.Amarr => _factionAmarr,
        Theming.FactionTheme.Caldari => _factionCaldari,
        Theming.FactionTheme.Minmatar => _factionMinmatar,
        _ => _factionGallente
    };

    private Theming.FactionTheme SelectedFaction() =>
        _factionAmarr.IsChecked == true ? Theming.FactionTheme.Amarr
        : _factionCaldari.IsChecked == true ? Theming.FactionTheme.Caldari
        : _factionMinmatar.IsChecked == true ? Theming.FactionTheme.Minmatar
        : Theming.FactionTheme.Gallente;

    private void UpdateHint()
    {
        if (_hintBlock is null)
            return;

        var dir = _gamelogDirBox.Text?.Trim();
        _hintBlock.Text = string.IsNullOrWhiteSpace(dir) ? $"Detected default: {_detectedDefault}"
            : Directory.Exists(dir) ? "Folder exists."
            : "Folder not found — it will be picked up once it appears.";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions { Title = "Select the EVE gamelog directory", AllowMultiple = false };

        var start = _gamelogDirBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        var folder = picked.FirstOrDefault();
        if (folder is not null)
        {
            _gamelogDirBox.Text = folder.TryGetLocalPath() ?? folder.Path.LocalPath;
            UpdateHint();
        }
    }

    private void OnAutoDetect(object? sender, RoutedEventArgs e)
    {
        _gamelogDirBox.Text = _detectedDefault;
        UpdateHint();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => RequestClose();

    private async void OnSave(object? sender, RoutedEventArgs e) => await ApplyAndCloseAsync(reimportSde: false);

    // Saves the current settings too (so nothing is lost), and signals the caller to run a forced SDE re-import.
    private async void OnReimportSde(object? sender, RoutedEventArgs e) => await ApplyAndCloseAsync(reimportSde: true);

    private async Task ApplyAndCloseAsync(bool reimportSde)
    {
        var result = BuildResult(reimportSde);
        RequestClose();

        // The watcher persists and applies its own opt-in, the way the local API server does, so the toggle does
        // not have to travel through SettingsResult to get back to the one object that owns the state.
        if (_clipboardWatch is not null && _clipboardWatch.IsSupported)
            await _clipboardWatch.SetEnabledAsync(_watchClipboardBox.IsChecked ?? false);

        if (_onApply is not null)
            await _onApply(result);
    }

    private void RequestClose()
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }

    // Opens the per-instance data directory (client DB, SDE store, caches) in the OS file browser via the platform
    // launcher (xdg-open / explorer / Finder under the hood) — no shell-out, no settings change, view stays open.
    private void OnShowDataFolder(object? sender, RoutedEventArgs e)
    {
        var path = Composition.ClientServices.DataDirectory();   // also ensures the directory exists
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    // One row per action: a label, a button showing its current gesture(s) (click to record a new one) and a Reset
    // button (enabled only once the action carries an override). Built in code, matching this window's existing
    // style, rather than an ItemsControl/row-view-model — there is no other data-bound list in this file to match.
    private void BuildShortcutRows()
    {
        _shortcutRegistry = Program.Services?.GetService<KeyboardShortcutRegistry>();
        _shortcutRowsPanel.Children.Clear();
        _shortcutGestureButtons.Clear();
        _shortcutResetButtons.Clear();

        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var label = new TextBlock
            {
                Text = KeyboardShortcutDefaults.DisplayNames[action], Width = 260,
                VerticalAlignment = VerticalAlignment.Center
            };

            var gestureButton = new Button { MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0) };
            gestureButton.Click += (_, _) => OnRecordShortcutClicked(action, gestureButton);
            gestureButton.AddHandler(KeyDownEvent, (_, e) => OnShortcutRecorderKeyDown(action, gestureButton, e), RoutingStrategies.Tunnel);
            gestureButton.LostFocus += (_, _) => CancelRecordingIfActive(action, gestureButton);

            var resetButton = new Button { Content = "Reset" };
            resetButton.Click += async (_, _) => await OnResetShortcutClickedAsync(action, gestureButton, resetButton);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            row.Children.Add(label);
            row.Children.Add(gestureButton);
            row.Children.Add(resetButton);
            _shortcutRowsPanel.Children.Add(row);

            _shortcutGestureButtons[action] = gestureButton;
            _shortcutResetButtons[action] = resetButton;
        }

        RefreshShortcutRows();
    }

    private void RefreshShortcutRows()
    {
        foreach (var (action, button) in _shortcutGestureButtons)
        {
            button.Content = _shortcutRegistry?.DisplayText(action) ?? "—";
            _shortcutResetButtons[action].IsEnabled = _shortcutRegistry?.IsOverridden(action) ?? false;
        }
    }

    private void OnRecordShortcutClicked(ShortcutAction action, Button button)
    {
        if (_shortcutRegistry is null) return;
        _recordingAction = action;
        button.Content = "Press a key…";
        button.Focus();
    }

    // Tunnelled so it sees the key before the button's own click/space-activation handling would.
    private void OnShortcutRecorderKeyDown(ShortcutAction action, Button button, KeyEventArgs e)
    {
        if (_recordingAction != action || _shortcutRegistry is null) return;
        e.Handled = true; // never let a key pressed while recording reach a tab/window shortcut underneath

        // A bare modifier press is not a gesture yet — keep waiting for the key it is held for.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;

        _recordingAction = null;
        if (e.Key == Key.Escape) { RefreshShortcutRows(); return; } // cancel recording, keep the previous gesture

        _ = ApplyRecordedGestureAsync(_shortcutRegistry, action, new KeyGesture(e.Key, e.KeyModifiers));
    }

    private void CancelRecordingIfActive(ShortcutAction action, Button button)
    {
        if (_recordingAction != action) return;
        _recordingAction = null;
        RefreshShortcutRows();
    }

    private async Task ApplyRecordedGestureAsync(KeyboardShortcutRegistry registry, ShortcutAction action, KeyGesture gesture)
    {
        var result = await registry.SetOverrideAsync(action, gesture);
        ShowShortcutMessage(result.IsSuccess ? null : result.Messages.FirstOrDefault()?.Text);
        RefreshShortcutRows();
    }

    private async Task OnResetShortcutClickedAsync(ShortcutAction action, Button button, Button resetButton)
    {
        if (_shortcutRegistry is null) return;
        await _shortcutRegistry.ResetToDefaultAsync(action);
        ShowShortcutMessage(null);
        RefreshShortcutRows();
    }

    private async void OnResetAllShortcuts(object? sender, RoutedEventArgs e)
    {
        if (_shortcutRegistry is null) return;
        await _shortcutRegistry.ResetAllToDefaultAsync();
        ShowShortcutMessage(null);
        RefreshShortcutRows();
    }

    private void ShowShortcutMessage(string? message)
    {
        _shortcutMessageBlock.Text = message ?? "";
        _shortcutMessageBlock.IsVisible = !string.IsNullOrEmpty(message);
    }

    private SettingsResult BuildResult(bool reimportSde)
    {
        var dir = _gamelogDirBox.Text?.Trim() ?? "";
        var shareLocation = _shareLocationBox.IsChecked ?? false;
        var shareBounty = _shareBountyBox.IsChecked ?? false;
        var shareCombat = _shareCombatBox.IsChecked ?? true;
        var loadTypeImages = _loadTypeImagesBox.IsChecked ?? false;
        var openFitDetailAfterImport = _openFitDetailAfterImportBox.IsChecked ?? true;
        var openFleetRunWindowImmediately = _openFleetRunWindowBox.IsChecked ?? false;
        var toastPosition = (Notifications.ToastPosition)(_toastPositionBox.SelectedIndex is { } i and >= 0 ? i : (int)Notifications.ToastPosition.TopRight);
        var enableLocalApi = _enableLocalApiBox.IsChecked ?? false;
        var localApiPort = int.TryParse(_localApiPortBox.Text, out var port) && port is > 0 and <= 65535
            ? port
            : LocalApi.LocalApiServer.DefaultPort;
        var checkUpdatesOnStartup = _checkUpdatesOnStartupBox.IsChecked ?? true;
        var autoPublishFleetRuns = _autoPublishFleetRunsBox?.IsChecked ?? true;
        var shareLoot = _shareLootBox?.IsChecked ?? false;
        var shareMining = _shareMiningBox?.IsChecked ?? false;
        return new SettingsResult(dir, shareLocation, shareBounty, shareCombat, loadTypeImages, SelectedFaction(), reimportSde, openFitDetailAfterImport, toastPosition, enableLocalApi, localApiPort, checkUpdatesOnStartup, openFleetRunWindowImmediately, autoPublishFleetRuns, shareLoot, shareMining);
    }
}
