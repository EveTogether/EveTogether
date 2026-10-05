using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.LocalApi.Widgets;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>Saved widgets (presets are read-only, customizing makes a copy) and the key gate on the <c>/w/&lt;id&gt;</c> page.</summary>
public class LocalApiWidgetTests
{
    private const string ApiKey = "stream-secret";

    [Fact]
    public async Task Update_BuiltInPreset_IsRefusedAndPresetStaysUnchanged()
    {
        using var instance = TestClientInstance.Create();
        var store = instance.Services.GetRequiredService<WidgetStore>();
        var preset = WidgetPresets.Find("live-dps") ?? throw new InvalidOperationException("live-dps preset missing");

        var updated = await store.UpdateAsync(preset with { Name = "Renamed" }, TestContext.Current.CancellationToken);
        var deleted = await store.DeleteAsync(preset.Id, TestContext.Current.CancellationToken);

        Assert.False(updated.IsSuccess);
        Assert.Equal(MessageCodes.PresetReadOnly, Assert.Single(updated.Messages).Code);
        Assert.False(deleted.IsSuccess);
        var reloaded = await store.GetAsync("live-dps", TestContext.Current.CancellationToken);
        Assert.Equal("Live DPS", reloaded?.Name);
    }

    [Fact]
    public async Task Create_FromPreset_SavesACopyUnderANewIdAndSignalsIt()
    {
        using var instance = TestClientInstance.Create();
        var store = instance.Services.GetRequiredService<WidgetStore>();
        var preset = WidgetPresets.Find("dps-graph") ?? throw new InvalidOperationException("dps-graph preset missing");
        var changes = new List<WidgetConfigChangedDto>();
        store.Changed += changes.Add;

        var created = await store.CreateAsync(preset with { Name = "My graph", Accent = "#ff0000", Scale = 150 },
            TestContext.Current.CancellationToken);

        var copy = created.Value ?? throw new InvalidOperationException("create failed");
        Assert.NotEqual(preset.Id, copy.Id);
        Assert.Matches("^[0-9a-f]{32}$", copy.Id);
        Assert.Equal(WidgetPreset.DpsGraph, copy.Preset);
        var all = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("DPS graph", Assert.Single(all, widget => widget.Id == preset.Id).Name);
        Assert.Equal("#ff0000", Assert.Single(all, widget => widget.Id == copy.Id).Accent);
        var change = Assert.Single(changes);
        Assert.Equal((copy.Id, WidgetChangeKind.Created), (change.Id, change.Change));
    }

    [Fact]
    public async Task Update_SavedWidget_PersistsAndSignalsTheNewConfig()
    {
        using var instance = TestClientInstance.Create();
        var store = instance.Services.GetRequiredService<WidgetStore>();
        var preset = WidgetPresets.Find("run-totals") ?? throw new InvalidOperationException("run-totals preset missing");
        var copy = (await store.CreateAsync(preset, TestContext.Current.CancellationToken)).Value
            ?? throw new InvalidOperationException("create failed");
        var changes = new List<WidgetConfigChangedDto>();
        store.Changed += changes.Add;

        var updated = await store.UpdateAsync(copy with { Theme = WidgetTheme.Ticker }, TestContext.Current.CancellationToken);

        Assert.True(updated.IsSuccess);
        Assert.Equal(WidgetTheme.Ticker, (await store.GetAsync(copy.Id, TestContext.Current.CancellationToken))?.Theme);
        Assert.Equal(WidgetTheme.Ticker, Assert.Single(changes).Config?.Theme);
    }

    [Fact]
    public async Task Create_InvalidAccent_IsRefusedAndNothingIsSaved()
    {
        using var instance = TestClientInstance.Create();
        var store = instance.Services.GetRequiredService<WidgetStore>();
        var preset = WidgetPresets.Find("live-dps") ?? throw new InvalidOperationException("live-dps preset missing");

        var created = await store.CreateAsync(preset with { Accent = "red;background:url(x)" }, TestContext.Current.CancellationToken);

        Assert.Equal(MessageCodes.ValidationFailed, Assert.Single(created.Messages).Code);
        Assert.Equal(WidgetPresets.All.Count, (await store.ListAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task WidgetPage_WithApiKeySet_NeedsTheKeyInTheQuery()
    {
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(LocalApiServer.ApiKeySettingKey, ApiKey), TestContext.Current.CancellationToken);
        var server = instance.Services.GetRequiredService<ILocalApiServer>();
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();

            var withoutKey = await http.GetAsync($"http://127.0.0.1:{port}/w/live-dps", TestContext.Current.CancellationToken);
            var withKey = await http.GetAsync($"http://127.0.0.1:{port}/w/live-dps?key={ApiKey}", TestContext.Current.CancellationToken);
            var unknown = await http.GetAsync($"http://127.0.0.1:{port}/w/no-such-widget?key={ApiKey}", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
            Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
            Assert.Equal("text/html", withKey.Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task WidgetPage_WithoutApiKey_IsServedForPresetAndSavedWidget()
    {
        using var instance = TestClientInstance.Create();
        var copy = (await instance.Services.GetRequiredService<WidgetStore>()
                .CreateAsync(WidgetPresets.All[0] with { Name = "Mine" }, TestContext.Current.CancellationToken)).Value
            ?? throw new InvalidOperationException("create failed");
        var server = instance.Services.GetRequiredService<ILocalApiServer>();
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var http = new HttpClient();

            var preset = await http.GetAsync($"http://127.0.0.1:{port}/w/fleet-dps", TestContext.Current.CancellationToken);
            var saved = await http.GetAsync($"http://127.0.0.1:{port}/w/{copy.Id}", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, preset.StatusCode);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    private static int _FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
