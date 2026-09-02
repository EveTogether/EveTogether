using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-137 — rewards land on <see cref="RunParameter"/> as a number, so a total is a SUM in SQL instead of
/// text parsed in the player's locale.</summary>
public sealed class RunRewardStorageTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Three level-4 missions: two where the time bonus was made and one where it was missed. The missed
    /// bonus keeps its row — the offer and its deadline are the only record that there was one — but with no amount,
    /// so it costs nothing in the total. That is the shape the column has to survive, not three runs that all paid.
    /// </summary>
    [AvaloniaFact]
    public async Task IskRewards_OverASeriesOfRuns_AddUpInSqlWithoutReadingTheTypedText()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await _SaveMissionRun(dispatcher, 90000001, 1_000_000m, 1_610_000m, cancellationToken);
        await _SaveMissionRun(dispatcher, 90000002, 1_250_000m, 2_000_000m, cancellationToken);
        await _SaveMissionRun(dispatcher, 90000003, 940_000m, null, cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        IQueryable<decimal?> total = db.Set<RunParameter>()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.Isk || parameter.ParameterKey == RunParameterKey.BonusIsk)
            .GroupBy(_ => 1)
            .Select(group => group.Sum(parameter => parameter.Amount));
        string sql = total.ToQueryString();

        Assert.Equal(6_800_000m, await total.SingleAsync(cancellationToken));
        // The whole total is one aggregate over the Amount column and the raw text is never read. The aggregate is
        // ef_sum on SQLite (EF stores decimal as TEXT there, as every ISK column in this project already does) and a
        // native SUM on the server providers, so the match is deliberately case-insensitive and unanchored.
        Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Amount\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TypedValue", sql, StringComparison.Ordinal);
        // The missed bonus is still on file, with the deadline it was offered under, so "how often did I make it"
        // has a denominator: COUNT(Amount) against COUNT(*) over the same rows.
        List<RunParameter> bonuses = await db.Set<RunParameter>()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.BonusIsk)
            .ToListAsync(cancellationToken);
        Assert.Equal(3, bonuses.Count);
        Assert.Equal(2, bonuses.Count(bonus => bonus.Amount is not null));
        Assert.All(bonuses, bonus => Assert.Equal(21600, bonus.BonusWindowSeconds));
    }

    /// <summary>An item reward is a type plus a count, and both have to come back — from the database and off the
    /// wire, which is where a new column gets silently dropped.</summary>
    [AvaloniaFact]
    public async Task ItemReward_ThroughStorageAndTheWire_KeepsItsCountAndItsType()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission, StartedAtUtc,
            1234, "Worlds Collide", 30000142, SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018721, MissionLevel: 4), cancellationToken);

        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(22), StartedAtUtc.AddMinutes(23), [], [], [],
            [new RunParameterInput
            {
                ParameterKey = RunParameterKey.Item,
                TypedValue = "5000 x Tritanium",
                Amount = 5000m,
                ItemTypeId = 34,
                ObservedAtUtc = StartedAtUtc.AddMinutes(22)
            }]), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run run = await db.Set<Run>().Include(candidate => candidate.Parameters).SingleAsync(cancellationToken);
        RunParameter stored = Assert.Single(run.Parameters);
        Assert.Equal(5000m, stored.Amount);
        Assert.Equal(34, stored.ItemTypeId);
        Assert.Equal("5000 x Tritanium", stored.TypedValue);
        Assert.Equal(SiteTypeSource.Mission, run.SiteTypeSource);
        Assert.Equal(3018721, run.AgentId);
        Assert.Equal(4, run.MissionLevel);

        Run roundTripped = RunWireData.FromEntity(run).ToEntity();

        RunParameter synchronized = Assert.Single(roundTripped.Parameters);
        Assert.Equal(5000m, synchronized.Amount);
        Assert.Equal(34, synchronized.ItemTypeId);
        Assert.Equal(SiteTypeSource.Mission, roundTripped.SiteTypeSource);
        Assert.Equal(3018721, roundTripped.AgentId);
        Assert.Equal(4, roundTripped.MissionLevel);
    }

    /// <summary>An escalation is a thing that happened, not a quantity. It has to be storable without a number.</summary>
    [AvaloniaFact]
    public async Task Observation_WithoutAnAmount_IsStillStored()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Sansha Refuge", 30000142), cancellationToken);

        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(18), StartedAtUtc.AddMinutes(19), [], [], [],
            [new RunParameterInput
            {
                ParameterKey = RunParameterKey.Escalation,
                TypedValue = "Command Relay Outpost, 23h57m",
                ObservedAtUtc = StartedAtUtc.AddMinutes(18)
            }]), cancellationToken);

        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunParameter stored = Assert.Single(await db.Set<RunParameter>().ToListAsync(cancellationToken));
        Assert.Equal(RunParameterKey.Escalation, stored.ParameterKey);
        Assert.Null(stored.Amount);
        Assert.Null(stored.ItemTypeId);
        Assert.Equal("Command Relay Outpost, 23h57m", stored.TypedValue);
    }

    /// <summary>Mission and site ids are disjunct spaces that reuse the same numbers. Two runs on the same
    /// <see cref="Run.SiteTypeId"/> from different spaces must stay tellable apart, or every later lookup guesses.</summary>
    [AvaloniaFact]
    public async Task SameSiteTypeId_FromTwoIdSpaces_StaysTellableApart()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 1234, "Sansha Refuge", 30000142), cancellationToken);
        await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Mission, StartedAtUtc, 1234, "Worlds Collide", 30000142,
            SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018721, MissionLevel: 4), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run mission = await db.Set<Run>().SingleAsync(run => run.SiteTypeId == 1234 && run.SiteTypeSource == SiteTypeSource.Mission, cancellationToken);
        Run site = await db.Set<Run>().SingleAsync(run => run.SiteTypeId == 1234 && run.SiteTypeSource == SiteTypeSource.Site, cancellationToken);
        Assert.Equal("Worlds Collide", mission.SiteName);
        Assert.Equal("Sansha Refuge", site.SiteName);
        Assert.Null(site.AgentId);
    }

    /// <summary>One level-4 mission: the payout, and the time bonus it was offered under. A null
    /// <paramref name="bonusIsk"/> is a bonus that was missed — the row stands, the amount does not.</summary>
    private static async Task _SaveMissionRun(IDispatcher dispatcher, long characterId, decimal payoutIsk, decimal? bonusIsk,
        CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Mission, StartedAtUtc,
            1234, "Worlds Collide", 30000142, SiteTypeSource: SiteTypeSource.Mission, AgentId: 3018721, MissionLevel: 4), cancellationToken);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(22), StartedAtUtc.AddMinutes(23), [], [], [],
        [
            new RunParameterInput
            {
                ParameterKey = RunParameterKey.Isk,
                TypedValue = $"{payoutIsk:N2} ISK",
                Amount = payoutIsk,
                ObservedAtUtc = StartedAtUtc.AddMinutes(22)
            },
            new RunParameterInput
            {
                ParameterKey = RunParameterKey.BonusIsk,
                TypedValue = "1.610.000 ISK if you complete the mission within 6 hours",
                Amount = bonusIsk,
                BonusWindowSeconds = 21600,
                ObservedAtUtc = StartedAtUtc.AddMinutes(22)
            }
        ]), cancellationToken);
        Assert.True(started.IsSuccess);
        Assert.True(saved.IsSuccess);
    }
}
