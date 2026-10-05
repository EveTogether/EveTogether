using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dispatcher = Avalonia.Threading.Dispatcher;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.LocalApi.Widgets;
using EveUtils.Client.Runs;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>
/// The widget manager (ET-434): the preset library and My widgets, the editor, the Local API state with a start
/// button, and the API key the widget URLs carry. Everything is saved through <see cref="WidgetStore"/>, so an open
/// OBS source follows a saved change over the socket.
/// </summary>
public sealed partial class WidgetManagerViewModel : ObservableObject, IRefreshableModule, IDisposable
{
    private const int ApiKeyBytes = 16;

    private readonly IServiceProvider _services;
    private readonly IDialogService _dialogs;
    private readonly WidgetStore _store;
    private readonly ILocalApiServer _server;
    private readonly RunsSession? _session;
    private IReadOnlyList<(int Id, string Name)> _characters = [];
    private string? _apiKey;

    public WidgetManagerViewModel(IServiceProvider services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        _store = services.GetRequiredService<WidgetStore>();
        _server = services.GetRequiredService<ILocalApiServer>();
        _session = services.GetService<RunsSession>();
        _store.Changed += _OnWidgetChanged;
        _server.StatusChanged += _OnApiStatusChanged;
        _ShowApiStatus();
    }

    public ObservableCollection<WidgetTileViewModel> Presets { get; } = [];
    public ObservableCollection<WidgetTileViewModel> MyWidgets { get; } = [];

    public bool HasMyWidgets => MyWidgets.Count > 0;
    public bool IsLibrary => Editor is null;
    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);
    public bool CanResetSession => _session is not null;
    public bool IncludeLocation { get; private set; }

    public string ApiKeyText => _apiKey is null ? "" : IsKeyRevealed ? _apiKey : new string('•', 16);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibrary))]
    private WidgetEditorViewModel? _editor;

    [ObservableProperty] private bool _isApiRunning;
    [ObservableProperty] private string _apiStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyText), nameof(RevealLabel))]
    private bool _isKeyRevealed;

    public string RevealLabel => IsKeyRevealed ? "Hide" : "Show";

    /// <summary>The API key as it stands, for the view's copy button; null when none is set.</summary>
    public string? ApiKey => _apiKey;

    public async Task LoadAsync()
    {
        using (var scope = _services.CreateScope())
        {
            var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
            _apiKey = settings.FirstOrDefault(setting => setting.Key == LocalApiServer.ApiKeySettingKey)?.Value is { Length: > 0 } key
                ? key
                : null;
            IncludeLocation = settings.FirstOrDefault(setting => setting.Key == LocalApiServer.IncludeLocationSettingKey)?.Value == "true";
        }

        IReadOnlyList<Character> characters = await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
        _characters = [.. characters
            .Where(character => character.EsiCharacterId is not null)
            .Select(character => (character.EsiCharacterId ?? 0, character.Name))
            .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)];

        _ShowKey();
        await _ReloadWidgetsAsync();
    }

    /// <summary>Opened again while open: the key or "Include my location" may have changed in the meantime.</summary>
    public void RefreshModule() => _ = LoadAsync();

    /// <summary>Url and size of a config as OBS needs them, with the current port and key.</summary>
    public WidgetDto WidgetFor(WidgetConfig config) =>
        WidgetDto.FromConfig(config, $"http://127.0.0.1:{_server.Status.Port}", _apiKey);

    public void CloseEditor() => Editor = null;

    public void ResetSession() => _session?.Reset();

    public async Task SaveAsync(WidgetEditorViewModel editor)
    {
        var config = editor.Build();
        var saved = editor.IsBuiltIn ? await _store.CreateAsync(config) : await _store.UpdateAsync(config);
        if (saved is not { IsSuccess: true, Value: { } widget })
        {
            editor.ShowStatus(saved.Messages.FirstOrDefault()?.Text ?? "The widget could not be saved.", isError: true);
            return;
        }

        if (editor.IsBuiltIn)
        {
            Editor = _NewEditor(widget);
            Editor.ShowStatus("Saved under My widgets, with its own URL.", isError: false);
            return;
        }
        editor.ShowStatus("Saved. An open OBS source picks it up by itself.", isError: false);
    }

    public async Task DeleteAsync(WidgetEditorViewModel editor)
    {
        var confirmed = await _dialogs.ConfirmAsync("Delete widget",
            $"Delete \"{editor.Name}\"? An OBS source that loads its URL shows nothing after this.");
        if (!confirmed) return;

        var deleted = await _store.DeleteAsync(editor.Id);
        if (!deleted.IsSuccess)
        {
            editor.ShowStatus(deleted.Messages.FirstOrDefault()?.Text ?? "The widget could not be deleted.", isError: true);
            return;
        }
        CloseEditor();
    }

    [RelayCommand]
    private async Task StartApiAsync()
    {
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Send(new SetSettingCommand(LocalApiServer.EnabledSettingKey, "true"));
        await _server.ApplyAsync(true, _server.Status.Port);
    }

    [RelayCommand]
    private void RevealKey() => IsKeyRevealed = !IsKeyRevealed;

    /// <summary>Creates a key, or replaces the one set; a running API restarts so the new key takes effect.</summary>
    [RelayCommand]
    private async Task RenewKeyAsync()
    {
        if (HasApiKey && !await _dialogs.ConfirmAsync("Renew API key",
                "Every OBS source and tool that uses the current key stops working until you give it the new URL or key.",
                "Renew"))
            return;

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(ApiKeyBytes)).ToLowerInvariant();
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Send(new SetSettingCommand(LocalApiServer.ApiKeySettingKey, key));
        if (_server.Status.Status is LocalApiStatus.Running)
            await _server.ApplyAsync(true, _server.Status.Port); // the key is read when the host starts

        _apiKey = key;
        _ShowKey();
        _RefreshUrls();
    }

    public void Dispose()
    {
        _store.Changed -= _OnWidgetChanged;
        _server.StatusChanged -= _OnApiStatusChanged;
    }

    private async Task _ReloadWidgetsAsync()
    {
        IReadOnlyList<WidgetConfig> widgets = await _store.ListAsync();
        _Fill(Presets, widgets.Where(widget => WidgetPresets.IsBuiltIn(widget.Id)));
        _Fill(MyWidgets, widgets.Where(widget => !WidgetPresets.IsBuiltIn(widget.Id)));
        OnPropertyChanged(nameof(HasMyWidgets));
    }

    private void _Fill(ObservableCollection<WidgetTileViewModel> tiles, IEnumerable<WidgetConfig> widgets)
    {
        tiles.Clear();
        foreach (var widget in widgets)
            tiles.Add(new WidgetTileViewModel(widget, WidgetFor(widget).Url, tile => Editor = _NewEditor(tile.Config)));
    }

    private WidgetEditorViewModel _NewEditor(WidgetConfig config) => new(this, config, _characters, IncludeLocation);

    private void _RefreshUrls()
    {
        foreach (var tile in Presets.Concat(MyWidgets))
            tile.Url = WidgetFor(tile.Config).Url;
        if (Editor is { } editor)
            editor.ShowUrl(WidgetFor(editor.Build()));
    }

    private void _ShowKey()
    {
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(ApiKeyText));
    }

    private void _ShowApiStatus()
    {
        var status = _server.Status;
        IsApiRunning = status.Status is LocalApiStatus.Running;
        ApiStatusText = status.Status switch
        {
            LocalApiStatus.Running => $"Local API running on {status.Url}",
            LocalApiStatus.PortInUse => $"Local API is not running: port {status.Port} is in use by another program.",
            LocalApiStatus.Error => $"Local API is not running: {status.Message}",
            _ => "Local API is off, so OBS cannot load these widgets."
        };
        _RefreshUrls();
    }

    private void _OnApiStatusChanged(LocalApiStatusSnapshot status) => Dispatcher.UIThread.Post(_ShowApiStatus);

    // Raised inside the save on the saving thread; the list is rebuilt on the UI thread, also for a change made through
    // the API by a script.
    private void _OnWidgetChanged(WidgetConfigChangedDto change) => Dispatcher.UIThread.Post(() => _ = _ReloadWidgetsAsync());
}
