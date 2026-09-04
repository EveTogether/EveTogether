using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-163: the manual run-start screen is a second production caller of <see cref="StartRunCommand"/>,
/// not a second run type — and it stamps its own facts rather than leaving them to be inferred from the clipboard
/// flow's shape.</summary>
public sealed class ManualRunStartTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SdeSite Site = new(4321, "Sansha's Nest", null, null, null, null, null, null, false, []);

    private static TestClientInstance CreateInstance() => TestClientInstance.Create(services =>
        services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor().AddSite(Site)));

    private static ManualRunStartViewModel CreateViewModel(TestClientInstance instance, long characterId = 90000002) =>
        new(instance.Services.GetRequiredService<IDispatcher>(), instance.Services.GetRequiredService<ISdeAccessor>(),
            [new Character("Manual Pilot", (int)characterId)]) { SelectedSite = Site };

    // AC-1's counterproof: a second command or a second run type for the manual path would break this — the
    // manual entry and the clipboard entry have to keep landing in the same Run table through the same command.
    [AvaloniaFact]
    public async Task ManualStart_AndClipboardStart_BothLandInTheSameRunsTable()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 1234, "Homefront",
            30000142, Origin: RunOrigin.Clipboard), cancellationToken);
        var vm = CreateViewModel(instance);

        await vm.StartCommand.ExecuteAsync(null);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Assert.True(vm.Started);
        Assert.Equal(2, await db.Set<Run>().CountAsync(cancellationToken));
    }

    // A caller that forgets Origin has to read as "we don't know" rather than silently become a claim about
    // where the run came from — the same failure a pre-ET-163 row would have shown under a Clipboard default.
    [AvaloniaFact]
    public async Task Origin_DefaultsToUnknown_WhenTheCallerDoesNotPassOne()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 1234, "Homefront",
            30000142), cancellationToken);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(RunOrigin.Unknown, run.Origin);
    }

    // AC-2's counterproof: deriving Origin from SiteName (or Signature, or anything else) instead of storing it
    // would mislabel this clipboard run — it has no site name, exactly the case that breaks a derived rule.
    [AvaloniaFact]
    public async Task Origin_IsStored_NotDerivedFromSiteName()
    {
        using var instance = CreateInstance();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc, 0, null, null,
            Origin: RunOrigin.Clipboard), cancellationToken);
        var vm = CreateViewModel(instance);

        await vm.StartCommand.ExecuteAsync(null);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run clipboardRun = await db.Set<Run>().SingleAsync(r => r.CharacterId == 90000001, cancellationToken);
        Run manualRun = await db.Set<Run>().SingleAsync(r => r.CharacterId == 90000002, cancellationToken);
        Assert.Null(clipboardRun.SiteName);
        Assert.Equal(RunOrigin.Clipboard, clipboardRun.Origin);
        Assert.Equal(RunOrigin.Manual, manualRun.Origin);
    }

    // AC-3's counterproof: stamping TimesCorrectedAtUtc here would claim a measured start was corrected, when a
    // backdated manual run was never measured at all.
    [AvaloniaFact]
    public async Task Backdated_DoesNotStampTimesCorrectedAtUtc()
    {
        using var instance = CreateInstance();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var vm = CreateViewModel(instance);
        vm.IsBackdated = true;
        vm.BackdatedDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        vm.BackdatedTime = new TimeSpan(18, 30, 0);

        await vm.StartCommand.ExecuteAsync(null);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Null(run.TimesCorrectedAtUtc);
        Assert.True(run.StartedAtUtc < DateTime.UtcNow.AddDays(-1), "the backdated time was not actually used");
    }

    // AC-4's counterproof: swapping SiteTypeSource for Mission (or vice-versa) would point SiteTypeId at the wrong
    // id space — site and mission ids reuse the same numbers.
    [AvaloniaFact]
    public async Task SelectedSite_LandsWithSiteTypeSourceSite()
    {
        using var instance = CreateInstance();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var vm = CreateViewModel(instance);

        await vm.StartCommand.ExecuteAsync(null);

        await using ClientDbContext db = await instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        Run run = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(Site.DungeonId, run.SiteTypeId);
        Assert.Equal(SiteTypeSource.Site, run.SiteTypeSource);
    }
}
