using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-144: renaming a fit stores the new name in its own column, deliberately leaving <c>RawJson</c> and the
/// content hash alone — the fit's identity hangs on its content, not on what it is called. Both places that show a
/// fit therefore have to read the stored name, not the one still standing in the JSON: the card in the browser and
/// the header of a freshly opened detail window.
/// </summary>
public class FitRenameShowsEverywhereTests
{
    private const string OriginalName = "Escalation 3/10 - LZ";
    private const string NewName = "Esc 0o";

    private const string RawJson =
        """{"fitting_id":7001,"name":"Escalation 3/10 - LZ","description":"","ship_type_id":587,"items":[{"type_id":2,"flag":"HiSlot0","quantity":1}]}""";

    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (var i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>Seeds one fit, opens the browser, renames through the repository and refreshes the Local tab —
    /// the rebuilt row is the thing under test.</summary>
    private static async Task<(FitBrowserViewModel Browser, RecordingDialogService Dialogs, LocalFitting Seeded, IFittingRepository Repo)>
        RenameAndRebuildAsync(TestClientInstance instance)
    {
        var repo = instance.Services.GetRequiredService<IFittingRepository>();
        await repo.UpsertAsync(new LocalFitting
        {
            OwnerId = "95600001", EsiFittingId = 7001, Name = OriginalName, ShipTypeId = 587,
            RawJson = RawJson, ImportedAt = DateTimeOffset.UtcNow
        });
        var seeded = (await repo.ListAllAsync()).Single();

        var vm = new MainWindowViewModel(instance.Services);
        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
        vm.LaunchModuleCommand.Execute("fits");
        Assert.True(await WaitForAsync(() => dialogs.LastFitBrowser is not null), "the fit browser never opened");
        var browser = dialogs.LastFitBrowser!;
        await browser.Tabs[0].EnsureLoadedAsync();

        await repo.UpdateMetadataAsync(seeded.Id, NewName, null, null);
        await browser.RefreshCommand.ExecuteAsync(null);   // the Local tab re-reads the database
        Dispatcher.UIThread.RunJobs();

        return (browser, dialogs, seeded, repo);
    }

    [AvaloniaFact]
    public async Task RenamedFit_ShowsTheNewNameOnItsCard_WithRawJsonAndHashUntouched()
    {
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IDialogService, RecordingDialogService>());
        var (browser, _, seeded, repo) = await RenameAndRebuildAsync(instance);

        var row = browser.Tabs[0].PagedRows.Single();
        Assert.Equal(NewName, row.Name);

        var after = await repo.FindByIdAsync(seeded.Id);
        Assert.Equal(seeded.RawJson, after!.RawJson);          // the JSON still carries the original name
        Assert.Contains(OriginalName, after.RawJson);
        Assert.Equal(seeded.ContentHash, after.ContentHash);   // renaming never changes the fit's identity
    }

    /// <summary>The detail window built fresh from the row — not the same instance that did the renaming, whose
    /// in-place metadata update would show the new name without ever reading it back.</summary>
    [AvaloniaFact]
    public async Task RenamedFit_ShowsTheNewNameInAReopenedDetailHeader()
    {
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IDialogService, RecordingDialogService>());
        var (browser, dialogs, _, _) = await RenameAndRebuildAsync(instance);

        await browser.OpenDetailCommand.ExecuteAsync(browser.Tabs[0].PagedRows.Single());
        Assert.True(await WaitForAsync(() => dialogs.LastFitDetail is not null), "the detail window never opened");

        Assert.Equal(NewName, dialogs.LastFitDetail!.Name);
    }
}
