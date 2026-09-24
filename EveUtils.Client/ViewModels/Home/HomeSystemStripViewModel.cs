using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Clipboard;
using EveUtils.Client.EveSettings;
using EveUtils.Client.LocalApi;
using EveUtils.Shared.App;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// The home's status line (ET-324): is the plumbing fine — prices, SDE, clipboard watch, EVE settings auto-sync, the local
/// API and the version. Events move what raises them; prices, auto-sync and the SDE are read off the UI thread at load
/// and once a minute, since nothing announces them.
/// </summary>
public sealed partial class HomeSystemStripViewModel : ObservableObject, IDisposable
{
    /// <summary>ESI refreshes the averages hourly; two missed refreshes is when the prices stop being "hourly".</summary>
    private static readonly TimeSpan PricesStale = TimeSpan.FromHours(2);

    private readonly IServiceProvider? _services;
    private readonly ClipboardWatchService? _clipboard;
    private readonly ILocalApiServer? _localApi;

    public HomeSystemStripViewModel(IServiceProvider? services)
    {
        _services = services;
        _clipboard = services?.GetService<ClipboardWatchService>();
        _localApi = services?.GetService<ILocalApiServer>();
        if (_clipboard is not null)
            _clipboard.StateChanged += _ShowClipboard;
        if (_localApi is not null)
            _localApi.StatusChanged += _OnLocalApiChanged;
        _ShowClipboard();
        _ShowLocalApi();
        VersionText = AppInfo.DisplayVersion;
    }

    [ObservableProperty] private string _pricesText = "Prices";
    [ObservableProperty] private bool _arePricesFresh;
    [ObservableProperty] private string _sdeText = "SDE";
    [ObservableProperty] private bool _hasSde;
    [ObservableProperty] private bool _isClipboardWatching;
    [ObservableProperty] private bool _isAutoSyncOn;
    [ObservableProperty] private bool _isLocalApiRunning;
    [ObservableProperty] private string _localApiText = "off";
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private string _updateText = "up to date";
    [ObservableProperty] private bool _isUpdateReady;

    public string ClipboardText => IsClipboardWatching ? "on" : "off";

    public string AutoSyncText => IsAutoSyncOn ? "on" : "off";

    partial void OnIsClipboardWatchingChanged(bool value) => OnPropertyChanged(nameof(ClipboardText));

    partial void OnIsAutoSyncOnChanged(bool value) => OnPropertyChanged(nameof(AutoSyncText));

    /// <summary>An update downloaded and waiting for a restart — the shell's own banner says the same.</summary>
    public void ShowUpdateReady(bool isReady)
    {
        IsUpdateReady = isReady;
        UpdateText = isReady ? "update ready, restart to apply" : "up to date";
    }

    /// <summary>What nothing announces: the price snapshot's age, the SDE build and whether auto-sync is on.</summary>
    public async Task ReadAsync()
    {
        if (_services is not { } services)
            return;

        (DateTimeOffset? pricesAt, SdeVersion? sde, bool autoSync) = await Task.Run(async () =>
        {
            DateTimeOffset? snapshot = services.GetService<IMarketPriceRepository>() is { } prices
                ? await prices.GetSnapshotTimeAsync()
                : null;
            SdeVersion? version = services.GetService<ISdeAccessor>() is { IsAvailable: true } accessor ? accessor.Version : null;
            bool enabled = services.GetService<EveSettingsPreferences>() is { } preferences
                           && (await preferences.LoadAutoSyncAsync()).Enabled;
            return (snapshot, version, enabled);
        });

        ArePricesFresh = pricesAt is { } at && DateTimeOffset.UtcNow - at < PricesStale;
        PricesText = pricesAt is null ? "Prices not loaded yet"
            : ArePricesFresh ? "Prices ESI average · hourly"
            : "Prices from " + pricesAt.Value.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        HasSde = sde is not null;
        SdeText = sde is { } build ? $"SDE {build.BuildNumber}" : "SDE not downloaded yet";
        IsAutoSyncOn = autoSync;
    }

    private void _ShowClipboard() => IsClipboardWatching = _clipboard?.IsWatching ?? false;

    private void _OnLocalApiChanged(LocalApiStatusSnapshot status) => Dispatcher.UIThread.Post(_ShowLocalApi);

    private void _ShowLocalApi()
    {
        IsLocalApiRunning = _localApi?.Status.Status == LocalApiStatus.Running;
        LocalApiText = IsLocalApiRunning ? $"on · port {_localApi?.Status.Port}" : "off";
    }

    public void Dispose()
    {
        if (_clipboard is not null)
            _clipboard.StateChanged -= _ShowClipboard;
        if (_localApi is not null)
            _localApi.StatusChanged -= _OnLocalApiChanged;
    }
}
