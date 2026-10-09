using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-493 — saving an abyssal run asks "Run another?", and the answer arms the next run through the window
/// the manual start already hands over, so no second arming mechanism exists to drift from the first.</summary>
public sealed class RunAnotherPromptTests
{
    private const string Question = "Run another?";

    [AvaloniaFact]
    public async Task SaveRun_AbyssalAndArmChosen_OpensAnArmedWindowForTheSamePilotWithTheSameFilament()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        harness.Dialogs.OnChoose = (title, _) => title == Question;
        ActivityWindowViewModel model = await StartAbyssalAsync(harness);

        await model.SaveRunCommand.ExecuteAsync(null);

        ActivityWindowViewModel next = Assert.Single(harness.Dialogs.ShownActivityWindows);
        await next.LoadAsync();
        Assert.Equal(ActivityKind.Abyssal, next.Kind);
        Assert.Equal(ActivityRunState.NotStarted, next.RunState);
        Assert.Null(next.RunId);
        Assert.Equal(ActivityWindowHarness.CharacterId, next.PickedCharacter?.Id);
        Assert.Equal(2, next.TierIndex);
        Assert.Equal(0, next.WeatherIndex);
    }

    [AvaloniaFact]
    public async Task SaveRun_AbyssalAndDoneChosen_ArmsNothing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await StartAbyssalAsync(harness);

        await model.SaveRunCommand.ExecuteAsync(null);

        Assert.Contains(harness.Dialogs.ChoicePrompts, prompt => prompt.Title == Question);
        Assert.Empty(harness.Dialogs.ShownActivityWindows);
        Assert.Equal(ActivityRunState.Saved, model.RunState);
    }

    [AvaloniaFact]
    public async Task SaveRun_ASite_AsksNothing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        harness.Dialogs.OnChoose = (_, _) => true;
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Site);
        await model.StartRunCommand.ExecuteAsync(null);

        await model.SaveRunCommand.ExecuteAsync(null);

        Assert.DoesNotContain(harness.Dialogs.ChoicePrompts, prompt => prompt.Title == Question);
        Assert.Empty(harness.Dialogs.ShownActivityWindows);
    }

    [AvaloniaFact]
    public async Task SaveRun_AbyssalSavedThroughACloseQuestion_AsksNothing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        harness.Dialogs.OnChoose = (_, _) => true;
        ActivityWindowViewModel model = await StartAbyssalAsync(harness);

        bool closed = await model.RequestCloseAsync();

        Assert.True(closed);
        Assert.DoesNotContain(harness.Dialogs.ChoicePrompts, prompt => prompt.Title == Question);
        Assert.Empty(harness.Dialogs.ShownActivityWindows);
    }

    private static async Task<ActivityWindowViewModel> StartAbyssalAsync(ActivityWindowHarness harness)
    {
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.Activity().SelectTierCommand.ExecuteAsync(2);
        await model.Activity().SelectWeatherCommand.ExecuteAsync(0);
        await model.StartRunCommand.ExecuteAsync(null);
        Assert.NotNull(model.RunId);
        return model;
    }
}
