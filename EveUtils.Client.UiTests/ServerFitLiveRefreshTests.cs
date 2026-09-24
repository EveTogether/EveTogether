using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Fittings;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-20: a fit shared or deleted by another member reached the client's bus but never the FITS browser, so the server
/// tab kept its old list until the user reloaded by hand. The event names the server it came from and only that
/// server's tab reloads, keeping the page the reader was on.
/// </summary>
public class ServerFitLiveRefreshTests
{
    private const string ServerA = "https://a.example";
    private const string ServerB = "https://b.example";

    private static EsiFitting Fit(string name) =>
        new(0, name, "", 587, [new EsiFittingItem(1, "HiSlot0", 1)]);

    private static FitRowViewModel Row(string name) => new(Fit(name), "Tester", FallbackNameResolver.Instance);

    private static FitBrowserTabViewModel ServerTab(string address, List<string> serverFits, Action? onLoad = null) =>
        new(address, address, tab =>
        {
            onLoad?.Invoke();
            tab.SetRows(serverFits.Select(Row));
            return Task.CompletedTask;
        });

    private static async Task PushAsync(IServiceProvider services, string eventType, object payload, string server)
    {
        IIntegrationEvent evt = services.GetRequiredService<EveUtils.Shared.Messaging.Wire.IEventTypeRegistry>()
            .Deserialize(eventType, JsonSerializer.Serialize(payload), 90250177)!;
        ((IServerSourcedEvent)evt).SourceServerAddress = server;
        await services.GetRequiredService<IEventBus>().PublishAsync(evt);
    }

    private static async Task SettleAsync(ServerFitLiveRefresh refresh)
    {
        Dispatcher.UIThread.RunJobs();
        await refresh.Settled;
    }

    [AvaloniaFact]
    public async Task AFitSharedOnServerA_ReloadsTabA_AndLeavesTabBAlone()
    {
        using var instance = TestClientInstance.Create();
        List<string> fitsOnA = ["Rifter", "Slasher"];
        List<string> fitsOnB = ["Merlin"];
        var reloadsOfB = 0;
        var tabA = ServerTab(ServerA, fitsOnA);
        var tabB = ServerTab(ServerB, fitsOnB, () => reloadsOfB++);
        await tabA.EnsureLoadedAsync();
        await tabB.EnsureLoadedAsync();
        Assert.Equal(2, tabA.TotalCount);
        var tabs = new[] { tabA, tabB };
        using var refresh = new ServerFitLiveRefresh(instance.Services.GetRequiredService<IEventBus>(),
            address => tabs.Single(t => t.ServerAddress == address).ReloadAsync(), (_, _) => Task.CompletedTask);

        fitsOnA.Add("Hecate");
        await PushAsync(instance.Services, "fittings.shared",
            new FitSharedPayload(1, "Hecate", 34317, "{}", "Jithran"), ServerA);
        await SettleAsync(refresh);

        Assert.Equal(3, tabA.TotalCount);
        Assert.Equal(1, tabB.TotalCount);
        Assert.Equal(1, reloadsOfB);
    }

    [AvaloniaFact]
    public async Task AFitDeletedOnServerA_DropsItFromTabA()
    {
        using var instance = TestClientInstance.Create();
        List<string> fitsOnA = ["Rifter", "Slasher"];
        var tabA = ServerTab(ServerA, fitsOnA);
        await tabA.EnsureLoadedAsync();
        using var refresh = new ServerFitLiveRefresh(instance.Services.GetRequiredService<IEventBus>(),
            _ => tabA.ReloadAsync(), (_, _) => Task.CompletedTask);

        fitsOnA.Remove("Slasher");
        await PushAsync(instance.Services, "fittings.deleted", new FitDeletedPayload(7), ServerA);
        await SettleAsync(refresh);

        Assert.Equal(1, tabA.TotalCount);
    }

    [AvaloniaFact]
    public async Task ABurstOfSharesFromOneServer_ReloadsItsTabOnce()
    {
        using var instance = TestClientInstance.Create();
        var reloads = 0;
        var tabA = ServerTab(ServerA, ["Rifter"], () => reloads++);
        await tabA.EnsureLoadedAsync();
        reloads = 0;
        TaskCompletionSource quiet = new();
        using var refresh = new ServerFitLiveRefresh(instance.Services.GetRequiredService<IEventBus>(),
            _ => tabA.ReloadAsync(), (_, token) => quiet.Task.WaitAsync(token));

        for (var i = 0; i < 3; i++)
        {
            await PushAsync(instance.Services, "fittings.shared",
                new FitSharedPayload(1, "Hecate", 34317, "{}", "Jithran"), ServerA);
            Dispatcher.UIThread.RunJobs();
        }
        quiet.SetResult();
        await refresh.Settled;

        Assert.Equal(1, reloads);
    }

    [AvaloniaFact]
    public async Task AReload_KeepsTheSearchAndThePageTheReaderWasOn()
    {
        List<string> fits = Enumerable.Range(1, 30).Select(i => $"Fit {i:00}").ToList();
        var tab = ServerTab(ServerA, fits);
        await tab.EnsureLoadedAsync();
        tab.PageSize = 10;
        tab.Search = "Fit";
        await tab.SearchRound;
        tab.NextPageCommand.Execute(null);
        Assert.Equal(2, tab.CurrentPage);

        fits.Add("Fit 31");
        await tab.ReloadAsync();

        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal("Fit", tab.Search);
        Assert.Equal(31, tab.TotalCount);
    }
}
