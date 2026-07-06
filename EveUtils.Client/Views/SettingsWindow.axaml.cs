using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.LocalApi;

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
    private readonly Func<SettingsResult, Task>? _onApply;

    // Cached at construction (the instances survive the module host re-parenting; FindControl on the window would
    // return null once the content is stolen for a docked tab).
    private TextBox _gamelogDirBox = null!;
    private TextBlock _hintBlock = null!;
    private CheckBox _shareLocationBox = null!, _shareBountyBox = null!, _shareCombatBox = null!;
    private CheckBox _loadTypeImagesBox = null!, _openFitDetailAfterImportBox = null!, _enableLocalApiBox = null!;
    private ComboBox _toastPositionBox = null!;
    private TextBox _localApiPortBox = null!;
    private Ellipse _localApiStatusDot = null!;
    private TextBlock _localApiStatusBlock = null!;
    private Button _localApiStartStopButton = null!, _localApiDocsButton = null!, _localApiWidgetButton = null!;
    private RadioButton _factionGallente = null!, _factionAmarr = null!, _factionCaldari = null!, _factionMinmatar = null!;
    private StackPanel _generalPanel = null!, _interfacePanel = null!, _privacyPanel = null!, _integrationsPanel = null!;

    /// <summary>Set by the module host so Save/Cancel dismiss the docked tab; null when floating (then we Close()).</summary>
    public Action? CloseRequested { get; set; }

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SettingsWindow(string currentDirectory, string detectedDefault, bool shareLocation, bool shareBounty, bool shareCombat, bool loadTypeImages, Theming.FactionTheme currentFaction, string sdeVersionLabel, bool openFitDetailAfterImport = true, Notifications.ToastPosition toastPosition = Notifications.ToastPosition.TopRight, bool enableLocalApi = false, int localApiPort = LocalApi.LocalApiServer.DefaultPort, string localApiStatusLabel = "", ILocalApiServer? localApiServer = null, Func<SettingsResult, Task>? onApply = null) : this()
    {
        _detectedDefault = detectedDefault;
        _localApi = localApiServer;
        _onApply = onApply;

        _gamelogDirBox = this.FindControl<TextBox>("GamelogDirBox")!;
        _hintBlock = this.FindControl<TextBlock>("HintBlock")!;
        _shareLocationBox = this.FindControl<CheckBox>("ShareLocationBox")!;
        _shareBountyBox = this.FindControl<CheckBox>("ShareBountyBox")!;
        _shareCombatBox = this.FindControl<CheckBox>("ShareCombatBox")!;
        _loadTypeImagesBox = this.FindControl<CheckBox>("LoadTypeImagesBox")!;
        _openFitDetailAfterImportBox = this.FindControl<CheckBox>("OpenFitDetailAfterImportBox")!;
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

        _gamelogDirBox.Text = string.IsNullOrWhiteSpace(currentDirectory) ? detectedDefault : currentDirectory;
        _gamelogDirBox.TextChanged += (_, _) => UpdateHint();

        _shareLocationBox.IsChecked = shareLocation;
        _shareBountyBox.IsChecked = shareBounty;
        _shareCombatBox.IsChecked = shareCombat;
        _loadTypeImagesBox.IsChecked = loadTypeImages;
        _openFitDetailAfterImportBox.IsChecked = openFitDetailAfterImport;
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

        FactionRadioFor(currentFaction).IsChecked = true;
        UpdateHint();

        this.FindControl<ListBox>("CategoryNav")!.SelectedIndex = 0; // show General first (fires OnCategoryChanged)
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
            : Directory.Exists(dir) ? "✓ folder exists"
            : "⚠️ folder not found — it will be picked up once it appears";
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

    private SettingsResult BuildResult(bool reimportSde)
    {
        var dir = _gamelogDirBox.Text?.Trim() ?? "";
        var shareLocation = _shareLocationBox.IsChecked ?? false;
        var shareBounty = _shareBountyBox.IsChecked ?? false;
        var shareCombat = _shareCombatBox.IsChecked ?? true;
        var loadTypeImages = _loadTypeImagesBox.IsChecked ?? false;
        var openFitDetailAfterImport = _openFitDetailAfterImportBox.IsChecked ?? true;
        var toastPosition = (Notifications.ToastPosition)(_toastPositionBox.SelectedIndex is { } i and >= 0 ? i : (int)Notifications.ToastPosition.TopRight);
        var enableLocalApi = _enableLocalApiBox.IsChecked ?? false;
        var localApiPort = int.TryParse(_localApiPortBox.Text, out var port) && port is > 0 and <= 65535
            ? port
            : LocalApi.LocalApiServer.DefaultPort;
        return new SettingsResult(dir, shareLocation, shareBounty, shareCombat, loadTypeImages, SelectedFaction(), reimportSde, openFitDetailAfterImport, toastPosition, enableLocalApi, localApiPort);
    }
}
