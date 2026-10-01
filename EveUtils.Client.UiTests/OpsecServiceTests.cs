using Avalonia.Headless.XUnit;
using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417: OPSEC is switched live from Settings and the global shortcut, and is remembered across restarts.</summary>
public sealed class OpsecServiceTests
{
    private static readonly KeyGesture ToggleDefault = new(Key.O, KeyModifiers.Control | KeyModifiers.Shift);

    [AvaloniaFact]
    public async Task FirstStart_IsOff()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var opsec = new OpsecService(client.Services);

        await opsec.InitializeAsync();

        Assert.False(opsec.IsEnabled);
    }

    [AvaloniaFact]
    public async Task SwitchedOn_IsStillOnAfterARestart()
    {
        string instance;
        using (TestClientInstance first = TestClientInstance.Create())
        {
            first.KeepDataOnDispose = true;
            instance = first.InstanceName;
            await new OpsecService(first.Services).SetEnabledAsync(true);
        }

        using TestClientInstance restarted = TestClientInstance.Create(instanceName: instance);
        var opsec = new OpsecService(restarted.Services);
        await opsec.InitializeAsync();

        Assert.True(opsec.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Switching_RaisesChangedOnce_AndNotForTheSameState()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var opsec = new OpsecService(client.Services);
        var changes = 0;
        opsec.Changed += () => changes++;

        await opsec.SetEnabledAsync(true);
        await opsec.SetEnabledAsync(true);
        await opsec.SetEnabledAsync(false);

        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public async Task Render_MasksOnlyWhileOn_AndAlwaysDropsTheMarkers()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var opsec = new OpsecService(client.Services);
        string text = $"{OpsecText.Mark("Jita")} · 3 jumps";

        Assert.Equal("Jita · 3 jumps", opsec.Render(text));

        await opsec.SetEnabledAsync(true);
        string masked = opsec.Render(text);
        Assert.DoesNotContain("Jita", masked);
        Assert.DoesNotContain(OpsecText.Open, masked);
        Assert.EndsWith(" · 3 jumps", masked);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_ClaimedAtStart_AndAPressTogglesOpsec()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var opsec = new OpsecService(client.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalOpsecHotKeyService(opsec, new KeyboardShortcutRegistry(client.Services), client.Services, source);

        await hotkey.InitializeAsync();
        Assert.Equal(ToggleDefault, source.RegisteredGesture);

        source.RaisePressed();
        await ActivityWindowHarness.WaitUntil(() => opsec.IsEnabled);
        Assert.True(opsec.IsEnabled);

        source.RaisePressed();
        await ActivityWindowHarness.WaitUntil(() => !opsec.IsEnabled);
        Assert.False(opsec.IsEnabled);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_Disabled_ReleasesTheClaim_AndStaysReleasedAfterARestart()
    {
        string instance;
        using (TestClientInstance first = TestClientInstance.Create())
        {
            first.KeepDataOnDispose = true;
            instance = first.InstanceName;
            var source = new FakeGlobalHotKeySource();
            using var hotkey = new GlobalOpsecHotKeyService(new OpsecService(first.Services),
                new KeyboardShortcutRegistry(first.Services), first.Services, source);
            await hotkey.InitializeAsync();

            await hotkey.SetEnabledAsync(false);
            Assert.Null(source.RegisteredGesture);
        }

        using TestClientInstance restarted = TestClientInstance.Create(instanceName: instance);
        var restartedSource = new FakeGlobalHotKeySource();
        using var restartedHotkey = new GlobalOpsecHotKeyService(new OpsecService(restarted.Services),
            new KeyboardShortcutRegistry(restarted.Services), restarted.Services, restartedSource);
        await restartedHotkey.InitializeAsync();

        Assert.Equal(0, restartedSource.RegisterCalls);
        Assert.False(restartedHotkey.IsEnabled);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_RebindInSettings_MovesTheClaim()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(client.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalOpsecHotKeyService(new OpsecService(client.Services), registry, client.Services, source);
        await hotkey.InitializeAsync();

        var rebound = new KeyGesture(Key.F9, KeyModifiers.Shift);
        Result recorded = await registry.SetOverrideAsync(ShortcutAction.ToggleOpsec, rebound);

        Assert.True(recorded.IsSuccess);
        Assert.Equal(rebound, source.RegisteredGesture);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_CtrlAlt_IsRefusedGlobally()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(client.Services);
        var source = new FakeGlobalHotKeySource();
        using var hotkey = new GlobalOpsecHotKeyService(new OpsecService(client.Services), registry, client.Services, source);
        await hotkey.InitializeAsync();

        await registry.SetOverrideAsync(ShortcutAction.ToggleOpsec, new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Alt));

        Assert.Null(source.RegisteredGesture);
        Assert.Contains("reserved", hotkey.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_HeldByAnotherProgram_IsSurfaced()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var source = new FakeGlobalHotKeySource
        {
            OnRegister = _ => Result.Failure(new ResultMessage(MessageSeverity.Error,
                MessageCodes.HotKeyUnavailable, "Already claimed by another program.", "Shortcuts"))
        };
        using var hotkey = new GlobalOpsecHotKeyService(new OpsecService(client.Services),
            new KeyboardShortcutRegistry(client.Services), client.Services, source);

        await hotkey.InitializeAsync();

        Assert.Equal("Already claimed by another program.", hotkey.LastFailure);
    }

    [AvaloniaFact]
    public async Task GlobalShortcut_Unsupported_NeverTouchesTheSource()
    {
        using TestClientInstance client = TestClientInstance.Create();
        var source = new FakeGlobalHotKeySource { IsSupported = false };
        using var hotkey = new GlobalOpsecHotKeyService(new OpsecService(client.Services),
            new KeyboardShortcutRegistry(client.Services), client.Services, source);

        await hotkey.InitializeAsync();

        Assert.Equal(0, source.RegisterCalls);
        Assert.Null(hotkey.LastFailure);
    }

    [AvaloniaFact]
    public void Registered_AsOneSharedInstance()
    {
        using TestClientInstance client = TestClientInstance.Create();

        Assert.Same(client.Services.GetRequiredService<IOpsecService>(), client.Services.GetRequiredService<IOpsecService>());
    }
}
