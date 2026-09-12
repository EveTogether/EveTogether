using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-231: confirming or typing a homefront's actual payout — never anything read from the wallet, only what the
/// pilot says (<see cref="SetHomefrontPayoutCommand"/>). The one thing this must never do is leave two
/// <see cref="RunParameterKey.FixedPayout"/> rows on the same run: <c>RewardIskContributor</c> sums every row with
/// that key, so a second write has to replace the first or the run's own payout would count itself twice.
/// </summary>
public sealed class SetHomefrontPayoutCommandTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 9, 18, 41, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task Handle_WritesOneFixedPayoutParameter()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000012, ActivityKind.Site, StartedAtUtc,
            1234, "Raid: Hall of Sacrifice", 30000142), cancellationToken);

        Result written = await dispatcher.Send(new SetHomefrontPayoutCommand(started.Value, 15_000_000m), cancellationToken);

        Assert.True(written.IsSuccess);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunParameter parameter = Assert.Single(await db.Set<RunParameter>()
            .Where(p => p.RunId == started.Value && p.ParameterKey == RunParameterKey.FixedPayout)
            .ToListAsync(cancellationToken));
        Assert.Equal(15_000_000m, parameter.Amount);
    }

    [AvaloniaFact]
    public async Task Handle_CalledTwice_ReplacesRatherThanDuplicates()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000013, ActivityKind.Site, StartedAtUtc,
            1234, "Raid: Hall of Sacrifice", 30000142), cancellationToken);
        await dispatcher.Send(new SetHomefrontPayoutCommand(started.Value, 15_000_000m), cancellationToken);

        // The pilot typed a correction — the entered figure replaces the earlier confirm, it does not add to it.
        Result corrected = await dispatcher.Send(new SetHomefrontPayoutCommand(started.Value, 11_250_000m), cancellationToken);

        Assert.True(corrected.IsSuccess);
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        RunParameter parameter = Assert.Single(await db.Set<RunParameter>()
            .Where(p => p.RunId == started.Value && p.ParameterKey == RunParameterKey.FixedPayout)
            .ToListAsync(cancellationToken));
        Assert.Equal(11_250_000m, parameter.Amount);
    }

    [AvaloniaFact]
    public async Task Handle_NegativeAmount_Fails()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000014, ActivityKind.Site, StartedAtUtc,
            1234, "Raid: Hall of Sacrifice", 30000142), cancellationToken);

        Result written = await dispatcher.Send(new SetHomefrontPayoutCommand(started.Value, -1m), cancellationToken);

        Assert.False(written.IsSuccess);
    }
}
