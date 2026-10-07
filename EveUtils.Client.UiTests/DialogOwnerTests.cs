using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public class DialogOwnerTests
{
    [AvaloniaFact]
    public async Task OwnerFor_TheRunWindowsViewModel_IsTheRunWindow()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        var main = new Window();
        main.Show();
        var dialogs = new DialogService();
        dialogs.SetOwner(main);
        var model = new ActivityWindowViewModel(ActivityKind.Site, harness.Services);
        dialogs.ShowActivityWindow(model);
        await ActivityWindowHarness.WaitUntil(() => model.RunLoot?.RunStatusMessage is not null);

        Window? owner = dialogs.OwnerFor(model);

        Assert.NotNull(owner);
        Assert.Same(dialogs.ActivityWindow, owner);
        Assert.NotSame(main, owner);

        dialogs.ActivityWindow?.Close();
        main.Close();
    }

    [AvaloniaFact]
    public async Task OwnerFor_NoRequester_FallsBackToTheMainWindow()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        var main = new Window();
        main.Show();
        var dialogs = new DialogService();
        dialogs.SetOwner(main);
        var model = new ActivityWindowViewModel(ActivityKind.Site, harness.Services);
        dialogs.ShowActivityWindow(model);
        await ActivityWindowHarness.WaitUntil(() => model.RunLoot?.RunStatusMessage is not null);

        Assert.Null(dialogs.OwnerFor(null));
        Assert.Null(dialogs.OwnerFor(new object()));

        dialogs.ActivityWindow?.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void EveryRequesterAwareDialog_OpensOverTheOwnerItResolves()
    {
        string source = System.IO.File.ReadAllText(HomefrontWindowFocusTests.SourcePath("EveUtils.Client/Dialogs/DialogService.cs"));

        foreach (string method in new[] { "ConfirmAsync", "ChooseAsync", "ShowMessageAsync", "PickCharactersAsync",
                     "PickFitAsync", "ShowEscalationDialogAsync" })
        {
            int start = source.IndexOf($"{method}(", System.StringComparison.Ordinal);
            int end = source.IndexOf("\n    public ", start + 1, System.StringComparison.Ordinal);
            string body = source.Substring(start, end < 0 ? source.Length - start : end - start);
            Assert.Contains("OwnerFor(owner)", body, System.StringComparison.Ordinal);
        }
    }
}
