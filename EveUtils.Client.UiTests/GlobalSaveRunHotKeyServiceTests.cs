using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-320: Ctrl+Shift+S claimed system-wide for exactly as long as a run can be saved. The real Win32 registration
/// (<see cref="WindowsGlobalHotKeySource"/>) is exercised only by compilation, the same way
/// <c>WindowsClipboardChangeSource</c> has no test of its own — real coverage is at this orchestration layer, via
/// a fake <see cref="IGlobalHotKeySource"/>, so a test run never claims (or fails to release) a real OS hotkey.
/// </summary>
public class GlobalSaveRunHotKeyServiceTests
{
    private static readonly KeyGesture SaveRunDefault = new(Key.S, KeyModifiers.Control | KeyModifiers.Shift);

    [AvaloniaFact]
    public async Task Arms_OnlyOnceARunCanBeSaved_AndDisarms_WhenTheWindowCloses()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window); // the same call DialogService._Open makes on the real window
        Assert.Equal(0, source.RegisterCalls); // no run yet — nothing to save, nothing to claim

        await window.StartRunCommand.ExecuteAsync(null); // flips IsSaveButtonVisible on
        Assert.Equal(1, source.RegisterCalls);
        Assert.Equal(SaveRunDefault, source.RegisteredGesture);

        harness.Dialogs.CloseActivityWindow();
        Assert.Equal(1, source.UnregisterCalls);
        Assert.Null(source.RegisteredGesture);
    }

    [AvaloniaFact]
    public async Task RebindingSaveRunInSettings_MovesTheGlobalClaimAlong()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);
        Assert.Equal(SaveRunDefault, source.RegisteredGesture);

        var newGesture = new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift);
        var result = await registry.SetOverrideAsync(ShortcutAction.SaveRun, newGesture);

        Assert.True(result.IsSuccess);
        Assert.Equal(newGesture, source.RegisteredGesture); // still armed, now on the new combination
    }

    [AvaloniaFact]
    public async Task Disabled_NeverClaimsTheCombination_AndReEnablingArmsImmediately()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();
        await hotkey.SetEnabledAsync(false);

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);
        Assert.Equal(0, source.RegisterCalls); // opted out — the in-window shortcut (ET-319) is the only path

        await hotkey.SetEnabledAsync(true);
        Assert.Equal(1, source.RegisterCalls); // a run is still open and saveable, so turning it on arms right away
    }

    [AvaloniaFact]
    public async Task Unsupported_NeverTouchesTheSource_AndReportsNoFailure()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource { IsSupported = false };
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);

        // ET-320 acceptance: on an unsupported platform the fallback is silent — no claim attempted, no error shown.
        Assert.Equal(0, source.RegisterCalls);
        Assert.Equal(0, source.UnregisterCalls);
        Assert.Null(hotkey.LastFailure);
    }

    [AvaloniaFact]
    public async Task CtrlAltOverride_IsRefusedGlobally_WithoutEverAskingTheSource()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        // Ctrl+Alt is reserved by the operator's own AutoHotkey script (ET-320's own valkuil) — recordable in the
        // general registry (it carries a modifier), but never to be claimed system-wide.
        var reserved = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt);
        var recorded = await registry.SetOverrideAsync(ShortcutAction.SaveRun, reserved);
        Assert.True(recorded.IsSuccess);

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);

        Assert.Equal(0, source.RegisterCalls);
        Assert.Contains("reserved", hotkey.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task RegistrationFailure_IsSurfaced_AndStillLeavesTheInWindowShortcutIntact()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource
        {
            OnRegister = _ => Result.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.HotKeyUnavailable, "Already claimed by another program.", "Shortcuts"))
        };
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);

        Assert.Equal("Already claimed by another program.", hotkey.LastFailure);
        // The registration failing changes nothing about the guarded save itself — SaveRunFromShortcut still works
        // through the button/ET-319 path, proven separately below.
        Assert.True(window.IsSaveButtonVisible);
    }

    /// <summary>ET-319's own guard, shared by every SaveRun path (ET-320): a second call while the first save is
    /// still in flight must not start an overlapping save.</summary>
    [AvaloniaFact]
    public async Task SaveRunFromShortcut_PressedAgainWhileAlreadySaving_IsANoOp()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        await window.StartRunCommand.ExecuteAsync(null);
        Assert.True(window.IsSaveButtonVisible);

        window.SaveRunFromShortcut(); // starts SaveRunAsync; IsSaving flips true before this call returns
        Assert.True(window.IsSaving);

        window.SaveRunFromShortcut(); // must see IsSaving and do nothing — no second, overlapping save
        Assert.True(window.IsSaving);

        // Let the one save actually in flight finish before the harness (and its DbContext factory) tears down —
        // AsyncRelayCommand.Execute fires SaveRunAsync in the background, and disposing mid-save is a test-hygiene
        // problem, not something ET-320 is about.
        await ActivityWindowHarness.WaitUntil(() => !window.IsSaving);
    }

    /// <summary>End-to-end wiring, not just the arm/disarm bookkeeping: a press reaching the fake source's
    /// <see cref="IGlobalHotKeySource.Pressed"/> event actually saves the run through the dispatcher hop.</summary>
    [AvaloniaFact]
    public async Task SourcePressed_WhileArmed_SavesTheTrackedRun()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        var registry = new KeyboardShortcutRegistry(harness.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalSaveRunHotKeyService(harness.Dialogs, registry, harness.Services, source);
        await hotkey.InitializeAsync();

        ActivityWindowViewModel window = await harness.OpenAsync();
        harness.Dialogs.ShowActivityWindow(window);
        await window.StartRunCommand.ExecuteAsync(null);
        Assert.Equal(1, source.RegisterCalls);

        source.RaisePressed();
        await ActivityWindowHarness.WaitUntil(() => window.IsSaving || !window.IsSaveButtonVisible);
        Assert.True(window.IsSaving || !window.IsSaveButtonVisible); // saving, or already past it and saved

        // Drain the save before the harness disposes (same reasoning as the no-op test above).
        await ActivityWindowHarness.WaitUntil(() => !window.IsSaving);
    }

    /// <summary>Fake standing in for the real Win32 registration — records what would have been claimed/released
    /// instead of ever touching a live OS hotkey from a test.</summary>
    private sealed class FakeGlobalHotKeySource : IGlobalHotKeySource
    {
        public bool IsSupported { get; set; } = true;
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public KeyGesture? RegisteredGesture { get; private set; }
        public Func<KeyGesture, Result>? OnRegister { get; set; }

        public event Action? Pressed;

        public Result Register(KeyGesture gesture)
        {
            RegisterCalls++;
            var result = OnRegister?.Invoke(gesture) ?? Result.Success();
            RegisteredGesture = result.IsSuccess ? gesture : null;
            return result;
        }

        public void Unregister()
        {
            UnregisterCalls++;
            RegisteredGesture = null;
        }

        public void RaisePressed() => Pressed?.Invoke();

        public void Dispose()
        {
        }
    }
}
