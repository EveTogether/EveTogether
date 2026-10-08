using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

public sealed class Et471RenderProbe
{
    private const int Own = 883434905;
    private const int Nova = 2629;
    private const int Mjolnir = 2647;
    private const int Loot = 47768;
    private const int Drone = 2488;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 18, 18, 29, 3, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Render()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(Nova, "Nova Fury Light Missile", 384, 8).Add(Mjolnir, "Mjolnir Fury Light Missile", 384, 8)
            .Add(Loot, "Tetrahedral Cell", 1088, 17).Add(Drone, "Hobgoblin II", 100, 18)));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            _Price(Nova, 1_150), _Price(Mjolnir, 1_100), _Price(Loot, 6_500_000), _Price(Drone, 450_000)
        ], Ct);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Own, ActivityKind.Abyssal, StartedAtUtc, 1234,
            "Abyssal Deadspace", 30000142, CharacterNameSnapshot: "RaymondKrah"), Ct);
        await _CaptureAsync(dispatcher, 0, LootCaptureRole.CargoBefore, (Nova, "Nova Fury Light Missile", 200),
            (Mjolnir, "Mjolnir Fury Light Missile", 150), (Drone, "Hobgoblin II", 5));
        await _CaptureAsync(dispatcher, 10, LootCaptureRole.CargoAfter, (Nova, "Nova Fury Light Missile", 47),
            (Mjolnir, "Mjolnir Fury Light Missile", 89), (Drone, "Hobgoblin II", 4), (Loot, "Tetrahedral Cell", 3));
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc, StartedAtUtc.AddSeconds(636), [],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 4_200_000 }], [], []), Ct);
        await using (ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Ct))
        {
            db.Set<RunCombatTimeline>().Add(new RunCombatTimeline
            {
                RunId = started.Value, Seconds = 636,
                HitTallies =
                {
                    new RunHitTally { Direction = DamageDirection.Outgoing, Counterparty = "Ephialtes Dissipator", Weapon = "Nova Fury Light Missile", Quality = HitQuality.Hits, Count = 92, Sum = 50_000, Min = 154, Max = 998 },
                    new RunHitTally { Direction = DamageDirection.Outgoing, Counterparty = "Ephialtes Dissipator", Weapon = "Mjolnir Fury Light Missile", Quality = HitQuality.Hits, Count = 61, Sum = 15_000, Min = 100, Max = 400 }
                }
            });
            await db.SaveChangesAsync(Ct);
        }

        await dispatcher.Send(new RebuildActivitySummariesCommand(), Ct);
        ActivityOverviewRowDto row = (await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value?.Single() ?? throw new InvalidOperationException();
        var viewModel = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId, sde: instance.Services.GetRequiredService<ISdeAccessor>(),
            nameOf: _ => "RaymondKrah", ownCharacterIds: new HashSet<long> { Own }, services: instance.Services);
        await viewModel.LoadAsync(Ct);

        var window = new ActivityDetailWindow(viewModel) { Width = 900, Height = 1900 };
        window.Show();
        for (int i = 0; i < 16; i++)
        {
            UiDispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
        string directory = Environment.GetEnvironmentVariable("ET471_SHOTS") ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, "run-detail-before-after.png"), new PngBitmapEncoderOptions());
        File.WriteAllText(Path.Combine(directory, "total.txt"), viewModel.TotalIskText);
        window.Close();
    }

    private static async Task _CaptureAsync(IDispatcher dispatcher, int minute, LootCaptureRole role,
        params (int TypeId, string Name, long Quantity)[] items) =>
        await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute),
            Source = LootCaptureSource.Pasted,
            Role = role,
            ContentHash = $"HASH-{minute}",
            Entries = [.. items.Select(item => new RunLootEntryInput
            {
                ItemTypeId = item.TypeId, Name = item.Name, Quantity = item.Quantity, LootKind = LootKind.Gained
            })]
        }), Ct);

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
