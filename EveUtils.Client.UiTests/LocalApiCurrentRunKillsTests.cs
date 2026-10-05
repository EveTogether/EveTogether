using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class LocalApiCurrentRunKillsTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task RunWithThreeBountyLines_ShowsThreeKills()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 1234, "Homefront", 30000142),
            TestContext.Current.CancellationToken);
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Pilot");
        for (int minute = 1; minute <= 3; minute++)
            await gamelog.AddBountyAsync("Pilot", new BountyEvent(StartedAtUtc.AddMinutes(minute), 100_000));

        CurrentRunDto current = Assert.Single(await _Runs(instance).GetCurrentAsync(TestContext.Current.CancellationToken) ?? []);

        Assert.Equal(3, current.Kills);
    }

    [AvaloniaFact]
    public async Task RunWithoutBountyLines_ShowsZeroKills()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 1234, "Homefront", 30000142),
            TestContext.Current.CancellationToken);

        CurrentRunDto current = Assert.Single(await _Runs(instance).GetCurrentAsync(TestContext.Current.CancellationToken) ?? []);

        Assert.Equal(0, current.Kills);
    }

    private static LocalApiRuns _Runs(TestClientInstance instance) =>
        new(instance.Services, new LocalApiPrivacy(instance.Services, false));
}
