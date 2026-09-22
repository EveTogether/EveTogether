using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Input;

/// <summary>
/// Ctrl+Shift+S (ET-319) claimed system-wide (ET-320), for exactly as long as there is a run it could save —
/// pasting a site's signature and pressing the key works with EVE Online in the foreground, no click into
/// EVE Together first. Armed the moment <see cref="ActivityWindowViewModel.IsSaveButtonVisible"/> turns true and
/// released the moment it turns false or the window closes, because <c>RegisterHotKey</c> claims the combination
/// exclusively — Ctrl+Shift+S is a common "Save as" binding elsewhere, so it is only taken from other programs
/// while this app actually has a use for it.
///
/// Windows only (<see cref="IGlobalHotKeySource.IsSupported"/>); everywhere else this stays permanently disarmed
/// and ET-319's own in-window shortcut is the only path, exactly as it was before this ticket.
/// </summary>
public sealed class GlobalSaveRunHotKeyService : ISingletonService, IDisposable
{
    /// <summary>Settings key for the opt-in. Default on: absent or anything but "false" means enabled.</summary>
    public const string EnabledSettingKey = "shortcut.save-run.global";

    private readonly IDialogService _dialogs;
    private readonly KeyboardShortcutRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly IGlobalHotKeySource _source;

    private ActivityWindowViewModel? _trackedViewModel;
    private bool _enabled = true;
    private bool _armed;

    public GlobalSaveRunHotKeyService(IDialogService dialogs, KeyboardShortcutRegistry registry,
        IServiceProvider services, IGlobalHotKeySource? source = null)
    {
        _dialogs = dialogs;
        _registry = registry;
        _services = services;
        _source = source ?? (OperatingSystem.IsWindows() ? new WindowsGlobalHotKeySource() : new UnsupportedGlobalHotKeySource());

        _dialogs.ActivityWindowChanged += OnActivityWindowChanged;
        _registry.Changed += OnRegistryChanged;
        _source.Pressed += OnPressed;
    }

    /// <summary>False where the OS has no system-wide hotkey API; the UI says so instead of offering a dead toggle.</summary>
    public bool IsSupported => _source.IsSupported;

    /// <summary>Whether the pilot wants this at all. Read together with <see cref="IsSupported"/>: disabled and
    /// unsupported look the same from outside (never armed) but are different reasons.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Why the last attempt to claim the combination failed, or null when it either succeeded or nothing
    /// is being claimed right now. Settings shows this so a combination already held elsewhere is visible rather
    /// than silently falling back to focus-only.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="LastFailure"/> changes, so Settings reflects a claim
    /// that failed (or recovered) while the window happens to be open.</summary>
    public event Action? StateChanged;

    /// <summary>Reads the persisted opt-in. Called once the UI is up, like every other InitializeAsync singleton.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetSettingsQuery(), cancellationToken);

        var saved = settings.FirstOrDefault(s => s.Key == EnabledSettingKey)?.Value;
        _enabled = !string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns the global claim on or off and remembers the choice.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(EnabledSettingKey, enabled ? "true" : "false"), cancellationToken);

        _enabled = enabled;
        _Reevaluate();
    }

    public void Dispose()
    {
        _dialogs.ActivityWindowChanged -= OnActivityWindowChanged;
        _registry.Changed -= OnRegistryChanged;
        _source.Pressed -= OnPressed;
        if (_trackedViewModel is not null)
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _source.Dispose();
    }

    private void OnActivityWindowChanged(ActivityWindowViewModel? viewModel)
    {
        if (_trackedViewModel is not null)
            _trackedViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _trackedViewModel = viewModel;

        if (_trackedViewModel is not null)
            _trackedViewModel.PropertyChanged += OnViewModelPropertyChanged;

        _Reevaluate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityWindowViewModel.IsSaveButtonVisible))
            _Reevaluate();
    }

    // A rebind in Settings has to move the claim along (ET-320 acceptance 4) rather than leave it sitting on a
    // combination the pilot just gave up.
    private void OnRegistryChanged(ShortcutAction action)
    {
        if (action == ShortcutAction.SaveRun && _armed)
            _Register();
    }

    // The notification arrives on the listener's own thread; the guarded save has to run on the UI thread, the
    // same jump ClipboardWatchService makes for its own notification.
    private void OnPressed() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _trackedViewModel?.SaveRunFromShortcut());

    private void _Reevaluate()
    {
        var shouldArm = IsSupported && _enabled && (_trackedViewModel?.IsSaveButtonVisible ?? false);
        if (shouldArm == _armed)
            return;

        _armed = shouldArm;
        if (_armed)
            _Register();
        else
        {
            _source.Unregister();
            LastFailure = null;
            StateChanged?.Invoke();
        }
    }

    private void _Register()
    {
        var gestures = _registry.EffectiveGestures(ShortcutAction.SaveRun);
        var gesture = gestures.Count == 1 ? gestures[0] : null;

        if (gesture is null)
        {
            // Disabled entirely, or (never true for SaveRun today, but harmless if it ever is) carrying more than
            // one default gesture — either way there is nothing single and certain to claim globally.
            _source.Unregister();
            LastFailure = null;
        }
        else if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) && gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Reserved system-wide by the operator's own AutoHotkey script — only claiming it globally would ever
            // steal it from that script, so only the global path refuses it; the in-window shortcut is unaffected.
            _source.Unregister();
            LastFailure = $"{gesture} is reserved outside the app and cannot be claimed globally. It still works while the run window has focus.";
        }
        else
        {
            var result = _source.Register(gesture);
            LastFailure = result.IsSuccess ? null : result.Messages.FirstOrDefault()?.Text;
        }

        StateChanged?.Invoke();
    }
}
