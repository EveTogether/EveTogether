using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Imaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Events;
using EveUtils.Shared.Modules.Fittings.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// FITS (ET-324): the newest fits shared on the coupled servers, and one line about the own library with IMPORT FROM
/// EVE. What each pilot flies right now is in PILOTS, not here. A shared fit reloads the shared list, a library change
/// the library line — each off the UI thread, and neither anything else.
/// </summary>
public sealed partial class HomeFitsViewModel : ObservableObject, IDisposable
{
    public const int Shown = 4;

    private readonly IServiceProvider? _services;
    private readonly HomeNavigation _navigation;
    private readonly INotifyCollectionChanged? _library;
    private readonly IDisposable? _sharedSubscription;
    private readonly IDisposable? _deletedSubscription;
    private bool _isLibraryReadPosted;

    /// <param name="library">The shell's own fit list: it changes on every import, and the library line follows it.</param>
    public HomeFitsViewModel(IServiceProvider? services, HomeNavigation navigation, INotifyCollectionChanged? library)
    {
        _services = services;
        _navigation = navigation;
        _library = library;
        if (_library is not null)
            _library.CollectionChanged += _OnLibraryChanged;
        IEventBus? bus = services?.GetService<IEventBus>();
        _sharedSubscription = bus?.Subscribe<FitSharedEvent>(_OnFitShared);
        _deletedSubscription = bus?.Subscribe<FitDeletedEvent>(_OnFitDeleted);
    }

    public ObservableCollection<HomeFitViewModel> Latest { get; } = [];

    [ObservableProperty] private bool _hasLatest;
    [ObservableProperty] private string _libraryText = string.Empty;

    [RelayCommand]
    private void OpenFits() => _navigation.LaunchModule("fits");

    [RelayCommand]
    private Task ImportFromEve() => _navigation.ImportFittings();

    public Task LoadAsync() => Task.WhenAll(ReadSharedAsync(), ReadLibraryAsync());

    public async Task ReadSharedAsync()
    {
        if (_services is not { } services)
            return;

        IReadOnlyList<(SharedFitInfo Fit, string ServerName, string HullName)> shared = await Task.Run(() => _ReadSharedAsync(services));
        Dictionary<(string, string, DateTimeOffset), HomeFitViewModel> shown = Latest.ToDictionary(fit => fit.Key);
        List<HomeFitViewModel> latest = [];
        foreach ((SharedFitInfo fit, string serverName, string hullName) in shared.Take(Shown))
        {
            if (shown.GetValueOrDefault((fit.Name, fit.SharedByCharacterName, fit.SharedAt)) is not { } row)
            {
                row = new HomeFitViewModel(fit.Name, fit.SharedByCharacterName, fit.ShipTypeId, hullName, serverName, fit.SharedAt);
                if (services.GetService<ITypeImageProvider>() is { } images)
                    _ = row.LoadHullIconAsync(images);
            }

            latest.Add(row);
        }

        Latest.ReconcileTo(latest);
        HasLatest = latest.Count > 0;
    }

    public async Task ReadLibraryAsync()
    {
        if (_services?.GetService<CqrsDispatcher>() is not { } dispatcher)
            return;

        IReadOnlyList<LocalFitting> fittings = await Task.Run(() => dispatcher.Query(new GetFittingsQuery()));
        DateTimeOffset monthStart = new(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);
        int hulls = fittings.Select(fitting => fitting.ShipTypeId).Distinct().Count();
        int newThisMonth = fittings.Count(fitting => fitting.ImportedAt >= monthStart);
        LibraryText = $"{fittings.Count} fit{(fittings.Count == 1 ? "" : "s")} · {hulls} hull{(hulls == 1 ? "" : "s")} · "
                      + $"{newThisMonth} new in {DateTime.Now.ToString("MMM", CultureInfo.InvariantCulture)}";
    }

    private void _OnFitShared(FitSharedEvent shared) => Dispatcher.UIThread.Post(() => _ = ReadSharedAsync());

    private void _OnFitDeleted(FitDeletedEvent deleted) => Dispatcher.UIThread.Post(() => _ = ReadSharedAsync());

    /// <summary>The shell's list changes an item at a time on a reload: one read after the last of them.</summary>
    private void _OnLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLibraryReadPosted)
            return;

        _isLibraryReadPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isLibraryReadPosted = false;
            _ = ReadLibraryAsync();
        }, DispatcherPriority.Background);
    }

    /// <summary>Off the UI thread: every coupled server's shared fits, newest first, each hull named once.</summary>
    private static async Task<IReadOnlyList<(SharedFitInfo, string, string)>> _ReadSharedAsync(IServiceProvider services)
    {
        if (services.GetService<ServerFitShareClient>() is not { } fitShare || services.GetService<IClientSessionStore>() is not { } sessions)
            return [];

        IServerRegistry? registry = services.GetService<IServerRegistry>();
        ISdeNameResolver names = FitNameResolverFactory.For(services);
        List<(SharedFitInfo Fit, string ServerName)> shared = [];
        foreach (string server in await sessions.ListServersAsync())
        {
            (bool ok, _, IReadOnlyList<SharedFitInfo> fits) = await fitShare.GetSharedFitsAsync(server);
            if (!ok)
                continue;

            string serverName = registry is null ? server : await registry.DisplayNameAsync(server);
            shared.AddRange(fits.Select(fit => (fit, serverName)));
        }

        Dictionary<int, string> hulls = [];
        return [.. shared
            .OrderByDescending(entry => entry.Fit.SharedAt)
            .Take(Shown)
            .Select(entry => (entry.Fit, entry.ServerName,
                hulls.TryGetValue(entry.Fit.ShipTypeId, out string? hull)
                    ? hull
                    : hulls[entry.Fit.ShipTypeId] = names.TypeName(entry.Fit.ShipTypeId)))];
    }

    public void Dispose()
    {
        _sharedSubscription?.Dispose();
        _deletedSubscription?.Dispose();
        if (_library is not null)
            _library.CollectionChanged -= _OnLibraryChanged;
    }
}

/// <summary>One shared fit: the hull icon, the fit's name, hull · who shared it, and when.</summary>
public sealed partial class HomeFitViewModel(
    string name, string sharedBy, int shipTypeId, string hullName, string serverName, DateTimeOffset sharedAt) : ObservableObject
{
    public (string, string, DateTimeOffset) Key => (Name, SharedBy, SharedAt);

    public string Name { get; } = name;

    public string SharedBy { get; } = sharedBy;

    public DateTimeOffset SharedAt { get; } = sharedAt;

    public string DetailText { get; } = $"{hullName} · {sharedBy}";

    public string Tooltip { get; } = $"{name}\n{hullName}, shared by {sharedBy} on {serverName}";

    public string DateText { get; } = sharedAt == default
        ? string.Empty
        : sharedAt.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture);

    [ObservableProperty] private Bitmap? _hullIcon;

    public async Task LoadHullIconAsync(ITypeImageProvider images)
    {
        if (shipTypeId > 0)
            HullIcon = await images.GetImageAsync(shipTypeId, TypeImageKind.Icon, 32);
    }
}
