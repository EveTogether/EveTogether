using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The live source of truth for keyboard shortcuts (ET-209): resolving a key press, recording an override, and —
/// the point of "save only the deviations" — persisting a row only when the recorded gesture actually differs from
/// the built-in default, so a later release's new default reaches anyone who never touched that action.
/// </summary>
public class KeyboardShortcutRegistryTests
{
    [AvaloniaFact]
    public void Defaults_ResolveWithoutAnyOverride()
    {
        var registry = new KeyboardShortcutRegistry(TestClientInstance.Create().Services);

        Assert.True(registry.TryResolve(new KeyGesture(Key.W, KeyModifiers.Control), out var action));
        Assert.Equal(ShortcutAction.CloseTab, action);
        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_RecordingTheExactDefault_PersistsNoRow()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);

        // CloseTab's own default is Ctrl+W — "recording" exactly that is not a deviation.
        var result = await registry.SetOverrideAsync(ShortcutAction.CloseTab, new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.True(result.IsSuccess);
        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
        var settings = await instance.Services.GetRequiredService<ISettingRepository>().ListAsync();
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("shortcut."));
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_ADeviation_PersistsExactlyOneRow_AndAppliesImmediately()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);
        var newGesture = new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift);

        var result = await registry.SetOverrideAsync(ShortcutAction.CloseTab, newGesture);

        Assert.True(result.IsSuccess);
        Assert.True(registry.IsOverridden(ShortcutAction.CloseTab));

        // The old default no longer resolves to anything; the new gesture resolves to the action, without a restart.
        Assert.False(registry.TryResolve(new KeyGesture(Key.W, KeyModifiers.Control), out _));
        Assert.True(registry.TryResolve(newGesture, out var resolved));
        Assert.Equal(ShortcutAction.CloseTab, resolved);

        var settings = await instance.Services.GetRequiredService<ISettingRepository>().ListAsync();
        Assert.Single(settings, s => s.Key.StartsWith("shortcut."));
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_RefusesASilentDoubleAssignment()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);

        // FocusSearch's default is Ctrl+F; asking to also bind CloseTab's default (Ctrl+W) to it would leave two
        // actions answering the same key, which acceptance 5 says must be refused, not silently allowed.
        var result = await registry.SetOverrideAsync(ShortcutAction.FocusSearch, new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.False(result.IsSuccess);
        Assert.False(registry.IsOverridden(ShortcutAction.FocusSearch));
        Assert.True(registry.TryResolve(new KeyGesture(Key.F, KeyModifiers.Control), out var stillFocusSearch));
        Assert.Equal(ShortcutAction.FocusSearch, stillFocusSearch);
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_Null_DisablesTheAction()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);

        var result = await registry.SetOverrideAsync(ShortcutAction.CloseTab, null);

        Assert.True(result.IsSuccess);
        Assert.Empty(registry.EffectiveGestures(ShortcutAction.CloseTab));
        Assert.False(registry.TryResolve(new KeyGesture(Key.W, KeyModifiers.Control), out _));
        Assert.Equal("—", registry.DisplayText(ShortcutAction.CloseTab));
    }

    [AvaloniaFact]
    public async Task ResetToDefaultAsync_RemovesTheOverride_AndItsPersistedRow()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);
        await registry.SetOverrideAsync(ShortcutAction.CloseTab, new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift));

        await registry.ResetToDefaultAsync(ShortcutAction.CloseTab);

        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
        Assert.True(registry.TryResolve(new KeyGesture(Key.W, KeyModifiers.Control), out var action));
        Assert.Equal(ShortcutAction.CloseTab, action);
        var settings = await instance.Services.GetRequiredService<ISettingRepository>().ListAsync();
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("shortcut."));
    }

    [AvaloniaFact]
    public async Task InitializeAsync_LoadsAPreviouslyPersistedOverride_OnAFreshInstance()
    {
        using var instance = TestClientInstance.Create();
        var first = new KeyboardShortcutRegistry(instance.Services);
        var newGesture = new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift);
        await first.SetOverrideAsync(ShortcutAction.CloseTab, newGesture);

        // A fresh registry — as if the app were restarted — only knows what InitializeAsync reads back.
        var second = new KeyboardShortcutRegistry(instance.Services);
        Assert.True(second.TryResolve(new KeyGesture(Key.W, KeyModifiers.Control), out _)); // still the default before init

        await second.InitializeAsync();

        Assert.True(second.IsOverridden(ShortcutAction.CloseTab));
        Assert.True(second.TryResolve(newGesture, out var resolved));
        Assert.Equal(ShortcutAction.CloseTab, resolved);
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_RejectsABareLetterKey()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);

        var result = await registry.SetOverrideAsync(ShortcutAction.CloseTab, new KeyGesture(Key.W, KeyModifiers.None));

        Assert.False(result.IsSuccess);
        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
    }

    [AvaloniaFact]
    public async Task SetOverrideAsync_RejectsATextEditingCombo()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);

        var result = await registry.SetOverrideAsync(ShortcutAction.CloseTab, new KeyGesture(Key.C, KeyModifiers.Control));

        Assert.False(result.IsSuccess);
        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
    }

    [AvaloniaFact]
    public async Task ResetAllToDefaultAsync_ClearsEveryOverride()
    {
        using var instance = TestClientInstance.Create();
        var registry = new KeyboardShortcutRegistry(instance.Services);
        await registry.SetOverrideAsync(ShortcutAction.CloseTab, new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift));
        await registry.SetOverrideAsync(ShortcutAction.FocusSearch, null);

        await registry.ResetAllToDefaultAsync();

        Assert.False(registry.IsOverridden(ShortcutAction.CloseTab));
        Assert.False(registry.IsOverridden(ShortcutAction.FocusSearch));
        var settings = await instance.Services.GetRequiredService<ISettingRepository>().ListAsync();
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("shortcut."));
    }
}
