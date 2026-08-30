using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Watches the system clipboard and hands the payloads it recognises — an EFT fit, an inventory listing — to the
/// features that subscribed. It is a system with subscribers, not a helper for one caller, which is why the
/// guarantees below live here and not in whatever happens to consume it.
///
/// It is off unless the user turned it on (<c>clipboard.watch</c>, absent = off), and it does not read the
/// clipboard at all while nothing is subscribed. While on and listened to it sees everything that is copied, so:
/// an unrecognised payload is dropped where it is read — never stored, buffered, logged or attached to an error
/// report — and no raw clipboard text ever leaves the process, not to a log file and not to the local API.
/// <see cref="Consumers"/> names the features currently listening, so the disclosure shown to the user is the
/// live truth rather than a maintained list.
/// </summary>
public sealed class ClipboardWatchService : ISingletonService, IDisposable
{
    /// <summary>Settings key for the user's opt-in. Absent or anything but "true" means off.</summary>
    public const string EnabledSettingKey = "clipboard.watch";

    private readonly IClipboardChangeSource _source;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly ILogger<ClipboardWatchService> _logger;

    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscribers = [];

    /// <param name="source">Platform change source; production passes nothing and gets the one for this OS.</param>
    public ClipboardWatchService(IDialogService dialogs, IServiceProvider services,
        ILogger<ClipboardWatchService> logger, IClipboardChangeSource? source = null)
    {
        _dialogs = dialogs;
        _services = services;
        _logger = logger;
        _source = source ?? CreatePlatformSource();
        _source.Changed += OnClipboardChanged;
    }

    /// <summary>False where the OS cannot report a clipboard change; the UI says so instead of offering a dead toggle.</summary>
    public bool IsSupported => _source.IsSupported;

    /// <summary>Whether the clipboard is being watched right now.</summary>
    public bool IsWatching { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="IsWatching"/> changes, so the status line can follow.</summary>
    public event Action? StateChanged;

    /// <summary>The features listening right now, by the name they registered under. Empty means nothing uses it.</summary>
    public IReadOnlyList<string> Consumers
    {
        get
        {
            lock (_gate)
                return [.. _subscribers.Select(subscription => subscription.FeatureName)];
        }
    }

    /// <summary>Reads the persisted opt-in and starts watching only if the user turned it on. Called once the UI is up.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadEnabledAsync(cancellationToken))
            StartWatching();
    }

    /// <summary>Turns the watcher on or off and remembers the choice.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(EnabledSettingKey, enabled ? "true" : "false"), cancellationToken);

        if (enabled)
            StartWatching();
        else
            StopWatching();
    }

    /// <summary>
    /// Registers a feature to receive recognised payloads. <paramref name="featureName"/> is shown to the user as
    /// part of what leans on the clipboard, so it names the feature, not the class. Dispose to unsubscribe.
    /// </summary>
    public IDisposable Subscribe(string featureName, Action<ClipboardCapture> handler)
    {
        var subscription = new Subscription(this, featureName, handler);
        lock (_gate)
            _subscribers.Add(subscription);
        return subscription;
    }

    public void Dispose()
    {
        _source.Changed -= OnClipboardChanged;
        _source.Dispose();
    }

    private static IClipboardChangeSource CreatePlatformSource() =>
        OperatingSystem.IsWindows() ? new WindowsClipboardChangeSource() : new UnsupportedClipboardChangeSource();

    private void StartWatching()
    {
        if (!_source.IsSupported || IsWatching)
            return;

        _source.Start();
        IsWatching = true;
        StateChanged?.Invoke();
    }

    private void StopWatching()
    {
        if (!IsWatching)
            return;

        _source.Stop();
        IsWatching = false;
        StateChanged?.Invoke();
    }

    // The notification arrives on the listener's own thread; the clipboard is read through the toplevel, which is
    // the UI thread's.
    private void OnClipboardChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = InspectAsync());

    private async Task InspectAsync()
    {
        // Stopping the source holds off new notifications but not one already queued here, and "off" has to mean
        // the clipboard is not read — not that it is read one last time.
        if (!IsWatching)
            return;

        // Nothing listening means there is nothing to read the clipboard for, so it is not read at all.
        var subscribers = Snapshot();
        if (subscribers.Length == 0)
            return;

        string? text;
        try
        {
            text = await _dialogs.GetClipboardTextAsync();
        }
        catch (Exception ex)
        {
            // The message carries no payload: an unreadable clipboard is worth knowing about, its contents are not.
            _logger.LogError(ex, "Could not read the clipboard after a change notification.");
            return;
        }

        var shape = ClipboardShapeRecogniser.Recognise(text);
        if (text is null || shape == ClipboardShape.Unrecognised)
            return; // dropped here: nothing kept, nothing buffered, nothing written down

        var capture = new ClipboardCapture(shape, text);
        foreach (var subscription in subscribers)
        {
            try
            {
                subscription.Handler(capture);
            }
            catch (Exception ex)
            {
                // One failing feature must not cost the others their payload — and the payload stays out of the log.
                _logger.LogError(ex, "Clipboard subscriber {Feature} failed on a {Shape} payload.",
                    subscription.FeatureName, shape);
            }
        }
    }

    private async Task<bool> ReadEnabledAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        IReadOnlyList<SettingDto> settings = await scope.ServiceProvider
            .GetRequiredService<IDispatcher>().Query(new GetSettingsQuery(), cancellationToken);

        var saved = settings.FirstOrDefault(setting => setting.Key == EnabledSettingKey)?.Value;
        return string.Equals(saved, "true", StringComparison.OrdinalIgnoreCase);
    }

    private Subscription[] Snapshot()
    {
        lock (_gate)
            return [.. _subscribers];
    }

    private sealed class Subscription(ClipboardWatchService owner, string featureName, Action<ClipboardCapture> handler)
        : IDisposable
    {
        public string FeatureName { get; } = featureName;

        public Action<ClipboardCapture> Handler { get; } = handler;

        public void Dispose()
        {
            lock (owner._gate)
                owner._subscribers.Remove(this);
        }
    }
}
