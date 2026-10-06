using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class LocalApiKillmailsTests : IDisposable
{
    private const int CharacterId = 77;

    private readonly TestClientInstance _instance = TestClientInstance.Create();

    [Theory]
    [InlineData(KillmailsLatestKind.All, new[] { 3, 2, 1 })]
    [InlineData(KillmailsLatestKind.Kills, new[] { 3, 1 })]
    [InlineData(KillmailsLatestKind.Losses, new[] { 2 })]
    public async Task Latest_KindFilter_ReturnsOnlyThatKind_NewestFirst(KillmailsLatestKind kind, int[] expectedIds)
    {
        await _RegisterPilotAsync();
        await _StoreAsync(_Killmail(1, isLoss: false, DateTime.UtcNow.AddHours(-3)), _Killmail(2, isLoss: true, DateTime.UtcNow.AddHours(-2)),
            _Killmail(3, isLoss: false, DateTime.UtcNow.AddHours(-1)));

        var latest = await _Queries(includeLocation: false).GetLatestKillmailsAsync(kind, 25, TestContext.Current.CancellationToken);

        Assert.Equal(expectedIds, latest.Select(killmail => killmail.KillmailId));
        Assert.All(latest, killmail => Assert.Equal(killmail.KillmailId == 2 ? "Loss" : "Kill", killmail.Kind));
    }

    [Fact]
    public async Task Latest_LocationIsNullUntilIncludeLocationIsOn()
    {
        await _RegisterPilotAsync();
        await _StoreAsync(_Killmail(1, isLoss: false, DateTime.UtcNow.AddMinutes(-5)));

        var hidden = await _Queries(includeLocation: false).GetLatestKillmailsAsync(KillmailsLatestKind.All, 1, TestContext.Current.CancellationToken);
        var shown = await _Queries(includeLocation: true).GetLatestKillmailsAsync(KillmailsLatestKind.All, 1, TestContext.Current.CancellationToken);

        Assert.Null(hidden.Single().SolarSystemId);
        Assert.Equal(30000142, shown.Single().SolarSystemId);
    }

    [Fact]
    public async Task Push_ImportOfOldMails_AnnouncesNothing_ButANewMailIs()
    {
        await _RegisterPilotAsync();
        var server = new LocalApiServer(_instance.Services.GetRequiredService<ISettingRepository>(), _instance.Services,
            NullLogger<LocalApiServer>.Instance);
        var port = _FreePort();
        try
        {
            await server.ApplyAsync(true, port, TestContext.Current.CancellationToken);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), TestContext.Current.CancellationToken);

            await _StoreAsync(_Killmail(1, isLoss: false, DateTime.UtcNow.AddDays(-30)));
            await _StoreAsync(_Killmail(2, isLoss: true, DateTime.UtcNow.AddMinutes(-3)));

            var announced = await _AnnouncedWithinAsync(socket, TimeSpan.FromSeconds(4));

            Assert.Equal([2], announced.Select(data => data.GetProperty("killmailId").GetInt32()));
            Assert.Equal("Loss", announced.Single().GetProperty("kind").GetString());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }

    // Reads for the whole window rather than stopping at the first hit, so a push that arrives late still counts.
    private static async Task<List<JsonElement>> _AnnouncedWithinAsync(WebSocket socket, TimeSpan window)
    {
        var announced = new List<JsonElement>();
        var buffer = new byte[16384];
        using var cts = new CancellationTokenSource(window);
        try
        {
            while (true)
            {
                var builder = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token);
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var root = JsonDocument.Parse(builder.ToString()).RootElement;
                if (root.GetProperty("type").GetString() == "killmail.added")
                    announced.Add(root.GetProperty("data"));
            }
        }
        catch (OperationCanceledException)
        {
            return announced;
        }
    }

    private LocalApiQueries _Queries(bool includeLocation) =>
        new(_instance.Services, new LocalApiPrivacy(_instance.Services, includeLocation));

    // An own import only ever exists for a registered character, and the local API reads only those (ET-371). Done
    // first, before any socket opens, so the registry change is not part of what a push test listens to.
    private Task _RegisterPilotAsync() => _instance.Services.GetRequiredService<ICharacterRegistry>()
        .AddOrUpdateAsync(new Character("Pilot", CharacterId), TestContext.Current.CancellationToken);

    private async Task _StoreAsync(params LocalKillmail[] killmails)
    {
        await using var scope = _instance.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new StoreKillmailsCommand(CharacterId, killmails), TestContext.Current.CancellationToken);
        Assert.True(stored.IsSuccess);
    }

    private static LocalKillmail _Killmail(int id, bool isLoss, DateTime timeUtc) => new()
    {
        CharacterId = CharacterId,
        KillmailId = id,
        Hash = $"hash{id}",
        KillmailTimeUtc = timeUtc,
        SolarSystemId = 30000142,
        IsLoss = isLoss,
        VictimShipTypeId = 587
    };

    private static int _FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => _instance.Dispose();
}
