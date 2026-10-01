using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Opsec;

/// <summary>
/// "Turn OPSEC mode on or off" claimed system-wide (ET-417), so a streamer can hide where they are with EVE Online in
/// the foreground — the moment it matters is rarely a moment EVE Together has focus. Unlike the save-run claim
/// (ET-320) it is held the whole time the app runs: there is no state in which hiding a location stops being useful.
///
/// Windows only (<see cref="IGlobalHotKeySource.IsSupported"/>); elsewhere the in-window shortcut is the only path.
/// </summary>
public sealed class GlobalOpsecHotKeyService : ISingletonService, IDisposable
{
    /// <summary>Settings key for the opt-in. Default on: absent or anything but "false" means enabled.</summary>
    public const string EnabledSettingKey = "shortcut.opsec.global";

    private readonly IOpsecService _opsec;
    private readonly KeyboardShortcutRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly IGlobalHotKeySource _source;
    private bool _enabled = true;

    public GlobalOpsecHotKeyService(IOpsecService opsec, KeyboardShortcutRegistry registry, IServiceProvider services,
        IGlobalHotKeySource? source = null)
    {
        _opsec = opsec;
        _registry = registry;
        _services = services;
        // Its own source, not the save-run one: a source holds one combination, and both are claimed at once.
        _source = source ?? (OperatingSystem.IsWindows() ? new WindowsGlobalHotKeySource() : new UnsupportedGlobalHotKeySource());

        _registry.Changed += OnRegistryChanged;
        _source.Pressed += OnPressed;
    }

    public bool IsSupported => _source.IsSupported;

    public bool IsEnabled => _enabled;

    /// <summary>Why claiming the combination failed, or null while it is held or deliberately not claimed.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Raised whenever <see cref="LastFailure"/> may have changed.</summary>
    public event Action? StateChanged;

    /// <summary>Reads the persisted opt-in and claims the combination when wanted. Called once the UI is up.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetSettingsQuery(), cancellationToken);

        var saved = settings.FirstOrDefault(s => s.Key == EnabledSettingKey)?.Value;
        _enabled = !string.Equals(saved, "false", StringComparison.OrdinalIgnoreCase);
        _Claim();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(EnabledSettingKey, enabled ? "true" : "false"), cancellationToken);

        _enabled = enabled;
        _Claim();
    }

    public void Dispose()
    {
        _registry.Changed -= OnRegistryChanged;
        _source.Pressed -= OnPressed;
        _source.Dispose();
    }

    private void OnRegistryChanged(ShortcutAction action)
    {
        if (action == ShortcutAction.ToggleOpsec)
            _Claim();
    }

    // Arrives on the listener's own thread; IOpsecService raises Changed for the UI, so it is switched from there.
    private void OnPressed() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = _opsec.SetEnabledAsync(!_opsec.IsEnabled));

    private void _Claim()
    {
        var gestures = _registry.EffectiveGestures(ShortcutAction.ToggleOpsec);
        var gesture = gestures.Count == 1 ? gestures[0] : null;

        if (!IsSupported || !_enabled || gesture is null)
        {
            _source.Unregister();
            LastFailure = null;
        }
        else if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) && gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // The same reservation the save-run claim honours (ET-320): Ctrl+Alt belongs to the operator's own
            // AutoHotkey script system-wide.
            _source.Unregister();
            LastFailure = $"{gesture} is reserved outside the app and cannot be claimed globally. It still works while EVE Together has focus.";
        }
        else
        {
            var result = _source.Register(gesture);
            LastFailure = result.IsSuccess ? null : result.Messages.FirstOrDefault()?.Text;
        }

        StateChanged?.Invoke();
    }
}
