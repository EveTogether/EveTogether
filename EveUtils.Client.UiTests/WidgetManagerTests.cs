using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Dispatcher = Avalonia.Threading.Dispatcher;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Widgets;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Widgets;
using EveUtils.Client.Views.Widgets;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>The widget manager (ET-434): customize saves a copy, location fields follow "Include my location", the
/// API key reaches every URL, and the session reset restarts the session. Frames go to <c>EVEUTILS_SHOT_DIR</c>.</summary>
public sealed class WidgetManagerTests
{
    [AvaloniaFact]
    public async Task Customize_PresetSaved_BecomesMyWidgetAndThePresetStaysAsItWas()
    {
        using var instance = TestClientInstance.Create();
        await _AddCharactersAsync(instance);
        var manager = await _OpenAsync(instance);
        var window = new WidgetManagerWindow(manager) { Width = 1280, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, "et434-library");

        Assert.Equal(8, manager.Presets.Count);
        Assert.Empty(manager.MyWidgets);

        manager.Presets.Single(tile => tile.Config.Id == "dps-graph").OpenCommand.Execute(null);
        var editor = manager.Editor ?? throw new InvalidOperationException("no editor");
        Assert.True(editor.IsBuiltIn);
        editor.Name = "My graph";
        editor.Accents.Single(accent => accent.Value == "#cb4d3e").PickCommand.Execute(null);
        editor.Backgrounds.Single(background => background.Value == "panel").PickCommand.Execute(null);
        editor.Scale = 150;
        editor.Characters[0].ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, "et434-editor-preset");

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(await _WaitForAsync(() => manager.MyWidgets.Count == 1));

        var saved = manager.Editor ?? throw new InvalidOperationException("no editor after save");
        Assert.False(saved.IsBuiltIn);
        Assert.Matches("^[0-9a-f]{32}$", saved.Id);
        var copy = manager.MyWidgets.Single().Config;
        Assert.Equal(("My graph", "#cb4d3e", WidgetBackground.Panel, 150), (copy.Name, copy.Accent, copy.Background, copy.Scale));
        Assert.Equal([90000001], copy.CharacterIds);
        Assert.Equal("DPS graph", manager.Presets.Single(tile => tile.Config.Id == "dps-graph").Config.Name);
        Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, "et434-editor-saved");
        saved.BackCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, "et434-library-my-widgets");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Editor_IncludeLocationOff_LocationFieldsCannotBePicked()
    {
        using var instance = TestClientInstance.Create();
        var manager = await _OpenAsync(instance);

        manager.Presets.Single(tile => tile.Config.Id == "live-dps").OpenCommand.Execute(null);

        var system = manager.Editor?.Fields.Single(field => field.Key == "system") ?? throw new InvalidOperationException();
        Assert.False(system.CanChoose);
        Assert.Contains("Include my location", manager.Editor.LocationNote);
    }

    [AvaloniaFact]
    public async Task Editor_IncludeLocationOn_LocationFieldIsSavedWithShowLocation()
    {
        using var instance = TestClientInstance.Create();
        await _SetAsync(instance, LocalApiServer.IncludeLocationSettingKey, "true");
        var manager = await _OpenAsync(instance);
        manager.Presets.Single(tile => tile.Config.Id == "live-dps").OpenCommand.Execute(null);
        var editor = manager.Editor ?? throw new InvalidOperationException();

        var system = editor.Fields.Single(field => field.Key == "system");
        system.IsOn = true;

        Assert.True(system.CanChoose);
        Assert.True(editor.Build().ShowLocation);
        Assert.Contains("system", editor.Build().Fields);
    }

    [AvaloniaFact]
    public async Task RenewKey_ReplacingAKey_AsksFirstAndEveryUrlCarriesTheNewKey()
    {
        using var instance = TestClientInstance.Create();
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };
        var manager = await _OpenAsync(instance, dialogs);
        manager.Presets[0].OpenCommand.Execute(null);
        var editor = manager.Editor ?? throw new InvalidOperationException();

        await manager.RenewKeyCommand.ExecuteAsync(null);
        var first = manager.ApiKey;
        await manager.RenewKeyCommand.ExecuteAsync(null);
        var second = manager.ApiKey ?? throw new InvalidOperationException("no key");

        Assert.Single(dialogs.ConfirmPrompts);
        Assert.NotEqual(first, second);
        Assert.Equal(second, await _SettingAsync(instance, LocalApiServer.ApiKeySettingKey));
        Assert.All(manager.Presets, tile => Assert.EndsWith($"?key={second}", tile.Url));
        Assert.EndsWith($"?key={second}", editor.Url);
        Assert.True(editor.UrlHasKey);

        editor.ToggleObsGuideCommand.Execute(null);
        var window = new WidgetManagerWindow(manager) { Width = 1280, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        OverlayShots.Capture(window, "et434-editor-obs-key");
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunTotals_SessionPeriod_ResetRestartsTheSession()
    {
        using var instance = TestClientInstance.Create();
        var session = instance.Services.GetRequiredService<RunsSession>();
        var startedBefore = session.StartedAtUtc;
        var manager = await _OpenAsync(instance);
        manager.Presets.Single(tile => tile.Config.Id == "run-totals").OpenCommand.Execute(null);
        var editor = manager.Editor ?? throw new InvalidOperationException();

        Assert.True(editor.ShowsSessionReset);
        editor.ResetSessionCommand.Execute(null);

        Assert.True(session.StartedAtUtc > startedBefore);
        editor.Options.Single(option => option.Key == "period").Choices.Single(choice => choice.Value == "today")
            .PickCommand.Execute(null);
        Assert.False(editor.ShowsSessionReset);
    }

    private static async Task<WidgetManagerViewModel> _OpenAsync(TestClientInstance instance, RecordingDialogService? dialogs = null)
    {
        var manager = new WidgetManagerViewModel(instance.Services, dialogs ?? new RecordingDialogService());
        await manager.LoadAsync();
        return manager;
    }

    private static async Task _AddCharactersAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Kaelen Voss", 90000001));
        await registry.AddOrUpdateAsync(new Character("Mira Tanaka-Holt", 90000002));
    }

    private static async Task _SetAsync(TestClientInstance instance, string key, string value)
    {
        using var scope = instance.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new SetSettingCommand(key, value));
    }

    private static async Task<string?> _SettingAsync(TestClientInstance instance, string key)
    {
        using var scope = instance.Services.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(setting => setting.Key == key)?.Value;
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (var attempt = 0; attempt < tries; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
