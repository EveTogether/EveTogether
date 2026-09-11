using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Input;

/// <summary>
/// The live source of truth for every keyboard shortcut (ET-209): resolves a pressed <see cref="KeyGesture"/> to its
/// <see cref="ShortcutAction"/>, and lets Settings rebind, disable or reset one. Only deviations from
/// <see cref="KeyboardShortcutDefaults"/> are persisted (one <c>shortcut.*</c> row per overridden action) — an
/// action nobody touched carries no row, so a later release changing its default reaches everyone who never
/// customised it. Mirrors <see cref="Clipboard.ClipboardWatchService"/>'s shape: a singleton read once at startup
/// (<see cref="InitializeAsync"/>) that keeps itself current afterwards without a restart.
/// </summary>
public sealed class KeyboardShortcutRegistry : ISingletonService
{
    private const string SettingKeyPrefix = "shortcut.";

    /// <summary>Persisted value meaning "this action has no shortcut" — distinct from every real gesture's text.</summary>
    private const string DisabledSentinel = "none";

    private static readonly ShortcutAction[] AllActions = Enum.GetValues<ShortcutAction>();

    // Reserved regardless of which action is being recorded — these never leave the text field they belong to.
    private static readonly KeyGesture[] ReservedForTextEditing =
    [
        new(Key.C, KeyModifiers.Control), new(Key.V, KeyModifiers.Control), new(Key.X, KeyModifiers.Control),
        new(Key.A, KeyModifiers.Control), new(Key.Z, KeyModifiers.Control), new(Key.Y, KeyModifiers.Control),
        new(Key.Z, KeyModifiers.Control | KeyModifiers.Shift), new(Key.Back, KeyModifiers.Control),
    ];

    private readonly IServiceProvider _services;

    // Present = the action was explicitly set by the user; value null = disabled (no shortcut at all).
    private readonly Dictionary<ShortcutAction, KeyGesture?> _overrides = new();
    private Dictionary<KeyGesture, ShortcutAction> _reverseLookup = new();

    public KeyboardShortcutRegistry(IServiceProvider services)
    {
        _services = services;
        RebuildReverseLookup();
    }

    /// <summary>Reads the persisted overrides. Called once the UI is up, like <c>ClipboardWatchService.InitializeAsync</c>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Query(new GetSettingsQuery(), cancellationToken);

        _overrides.Clear();
        foreach (var action in AllActions)
        {
            var row = settings.FirstOrDefault(s => s.Key == KeyFor(action));
            if (row is null) continue;

            if (row.Value == DisabledSentinel) { _overrides[action] = null; continue; }
            if (TryParseGesture(row.Value) is { } gesture) _overrides[action] = gesture;
            // An unparsable value is treated as if the row were absent (falls back to the default) rather than
            // failing startup over one corrupted setting.
        }
        RebuildReverseLookup();
    }

    /// <summary>The gesture(s) that fire this action right now — the recorded override, or the built-in default(s)
    /// when nothing was recorded. Empty means the action currently has no shortcut at all.</summary>
    public IReadOnlyList<KeyGesture> EffectiveGestures(ShortcutAction action)
    {
        if (_overrides.TryGetValue(action, out var overridden))
            return overridden is null ? [] : [overridden];
        return KeyboardShortcutDefaults.Gestures[action];
    }

    /// <summary>True once the user has recorded, disabled or otherwise touched this action (so Settings can offer
    /// "reset to default" only where it would change something).</summary>
    public bool IsOverridden(ShortcutAction action) => _overrides.ContainsKey(action);

    /// <summary>What the Settings row reads for this action — the gesture(s) joined together, or an em dash when disabled.</summary>
    public string DisplayText(ShortcutAction action)
    {
        var gestures = EffectiveGestures(action);
        return gestures.Count == 0 ? "—" : string.Join(" or ", gestures.Select(g => g.ToString()));
    }

    /// <summary>Resolves a key press to the action it fires, if any.</summary>
    public bool TryResolve(KeyGesture pressed, out ShortcutAction action) => _reverseLookup.TryGetValue(pressed, out action);

    /// <summary>The other action already bound to <paramref name="gesture"/>, if any — used to refuse a silent
    /// double assignment rather than let two actions answer to the same key (ET-209 acceptance 5).</summary>
    public ShortcutAction? FindConflict(KeyGesture gesture, ShortcutAction excluding) =>
        AllActions.Where(a => a != excluding && EffectiveGestures(a).Any(g => g.Equals(gesture)))
            .Select(a => (ShortcutAction?)a)
            .FirstOrDefault();

    /// <summary>Records a new gesture for an action, or pass null to disable it entirely. Refused (no write) on a
    /// conflict or an unsafe combination — the caller shows why via the returned <see cref="Result"/>.</summary>
    public async Task<Result> SetOverrideAsync(ShortcutAction action, KeyGesture? gesture, CancellationToken cancellationToken = default)
    {
        if (gesture is not null)
        {
            if (!IsRecordable(gesture))
                return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    "Shortcuts need Ctrl, Alt or Shift, or must be a function key.", "Shortcuts"));

            if (ReservedForTextEditing.Any(g => g.Equals(gesture)))
                return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{gesture} is reserved for text editing.", "Shortcuts"));

            if (FindConflict(gesture, action) is { } conflict)
                return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{gesture} is already used by \"{KeyboardShortcutDefaults.DisplayNames[conflict]}\".", "Shortcuts"));
        }

        using var scope = _services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        if (IsDefault(action, gesture))
        {
            await dispatcher.Send(new DeleteSettingCommand(KeyFor(action)), cancellationToken);
            _overrides.Remove(action);
        }
        else
        {
            await dispatcher.Send(new SetSettingCommand(KeyFor(action), gesture is null ? DisabledSentinel : gesture.ToString()!), cancellationToken);
            _overrides[action] = gesture;
        }

        RebuildReverseLookup();
        return Result.Success();
    }

    /// <summary>Drops the override, if any, so the action goes back to answering to its built-in default(s).</summary>
    public async Task ResetToDefaultAsync(ShortcutAction action, CancellationToken cancellationToken = default)
    {
        if (!_overrides.ContainsKey(action)) return;

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new DeleteSettingCommand(KeyFor(action)), cancellationToken);
        _overrides.Remove(action);
        RebuildReverseLookup();
    }

    public async Task ResetAllToDefaultAsync(CancellationToken cancellationToken = default)
    {
        foreach (var action in _overrides.Keys.ToList())
            await ResetToDefaultAsync(action, cancellationToken);
    }

    // A recorded gesture that exactly reproduces a single-gesture default is not a deviation — nothing to persist.
    // An action with more than one default gesture (Refresh) can never be reproduced by recording one key, so
    // recording there is always an override; ResetToDefaultAsync is its way back.
    private static bool IsDefault(ShortcutAction action, KeyGesture? gesture)
    {
        if (gesture is null) return false;
        var defaults = KeyboardShortcutDefaults.Gestures[action];
        return defaults.Count == 1 && defaults[0].Equals(gesture);
    }

    private static bool IsRecordable(KeyGesture gesture) =>
        gesture.KeyModifiers != KeyModifiers.None || gesture.Key is >= Key.F1 and <= Key.F24 || gesture.Key == Key.Escape;

    private static KeyGesture? TryParseGesture(string value)
    {
        try { return KeyGesture.Parse(value); }
        catch (Exception) { return null; }
    }

    private void RebuildReverseLookup()
    {
        var map = new Dictionary<KeyGesture, ShortcutAction>();
        foreach (var action in AllActions)
            foreach (var gesture in EffectiveGestures(action))
                map[gesture] = action; // defaults never collide by construction; a conflicting override is refused before it is ever stored
        _reverseLookup = map;
    }

    internal static string KeyFor(ShortcutAction action)
    {
        var name = action.ToString();
        var key = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) key.Append('-');
            key.Append(char.ToLowerInvariant(name[i]));
        }
        return SettingKeyPrefix + key;
    }
}
