using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using StoredRunState = EveUtils.Shared.Modules.Runs.Enums.RunState;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-250: in a run whose clock is per pilot (ET-243), every own toon riding alongside the acting one (ET-210)
/// times its own leg off its own crossing — each has its own client and comes out, or picks a leg back up, on its
/// own moment, never the acting character's. SAVE still saves the whole group together; only the live timing splits.
/// </summary>
public sealed class AbyssalOwnPilotLegsTests
{
    private const int Pilot = 90000010;
    private const int Alt = 90000011;
    private const int Aphend = 30002718;       // an ordinary high-sec system
    private const int AbyssalRoom = 32000042;  // inside ADR01's range

    /// <summary>Acceptance (ET-250): one toon comes out of the pocket earlier than the others. Its own row stops;
    /// the acting pilot's own clock, still in the pocket, is untouched.</summary>
    [AvaloniaFact]
    public async Task AnAltThatComesOutEarlier_StopsOnlyItsOwnRow()
    {
        var watch = new PerCharacterLocationWatch();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IEsiLocationMonitor>(watch));
        // Registered, so _RefreshParticipantsAsync names this row "Alt" — the same name the gamelog watch below
        // is mapped under, and the name _RefreshOwnPilotLegs reads its own snapshot by.
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Alt", Alt));
        GamelogClientService gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(Alt, "Alt");
        // A named system the gamelog already knows, same as a jump line would have written — the "coming out"
        // reading only reads as outside once a location is known (ET-62's own "cannot tell no-answer from outside"
        // rule), same as the acting character's own crossing already requires.
        gamelog.SetLocation("Alt", "Aphend", DateTime.UtcNow.AddMinutes(-30));

        using var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        window.UseCharacter(Pilot, "Pilot");
        window.UseAdditionalCharacters([(Alt, "Alt")]);
        await window.LoadAsync();
        await window.StartRunCommand.ExecuteAsync(null);
        DateTime pilotIn = window.AnchorUtc!.Value;

        // The anchor a crossing writes is the last poll seen outside, not the moment noticed (ET-62) — same floor
        // the acting character's own entry already uses.
        DateTime altSeenOutsideAtUtc = pilotIn.AddMinutes(2);
        DateTime altCrossingNoticedAtUtc = altSeenOutsideAtUtc.AddSeconds(6);
        watch.Report(Alt, Aphend, altSeenOutsideAtUtc);
        watch.Report(Alt, AbyssalRoom, altCrossingNoticedAtUtc);
        Guid altRunId = await _WaitForOwnLegAsync(
            window, instance, altCrossingNoticedAtUtc, excludingRunId: window.RunId!.Value);

        // The alt comes out first, on its own client — the pilot's own clock keeps going.
        DateTime altOut = pilotIn.AddMinutes(8);
        watch.Report(Alt, Aphend, altOut);
        Run altRun = await _WaitForStateAsync(window, instance, altOut, altRunId, StoredRunState.Stopped);

        Assert.Equal(altOut, altRun.StoppedAtUtc);
        Assert.Equal(altSeenOutsideAtUtc, altRun.StartedAtUtc);
        Assert.Equal(ActivityRunState.Running, window.RunState);
        Assert.Null(window.StoppedAtUtc);
    }

    /// <summary>Acceptance (ET-250): after coming out, the same toon crosses back in before the group has saved or
    /// discarded — its own row is picked back up rather than a second one starting beside it.</summary>
    [AvaloniaFact]
    public async Task AnAltThatComesBackIn_ResumesItsOwnRow()
    {
        var watch = new PerCharacterLocationWatch();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IEsiLocationMonitor>(watch));
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Alt", Alt));
        GamelogClientService gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(Alt, "Alt");
        gamelog.SetLocation("Alt", "Aphend", DateTime.UtcNow.AddMinutes(-30));

        using var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        window.UseCharacter(Pilot, "Pilot");
        window.UseAdditionalCharacters([(Alt, "Alt")]);
        await window.LoadAsync();
        await window.StartRunCommand.ExecuteAsync(null);
        DateTime pilotIn = window.AnchorUtc!.Value;

        DateTime altSeenOutsideAtUtc = pilotIn.AddMinutes(1);
        DateTime altCrossingNoticedAtUtc = altSeenOutsideAtUtc.AddSeconds(6);
        watch.Report(Alt, Aphend, altSeenOutsideAtUtc);
        watch.Report(Alt, AbyssalRoom, altCrossingNoticedAtUtc);
        Guid altRunId = await _WaitForOwnLegAsync(
            window, instance, altCrossingNoticedAtUtc, excludingRunId: window.RunId!.Value);

        DateTime altOut = pilotIn.AddMinutes(3);
        watch.Report(Alt, Aphend, altOut);
        Run stopped = await _WaitForStateAsync(window, instance, altOut, altRunId, StoredRunState.Stopped);
        Assert.Equal(StoredRunState.Stopped, stopped.State);
        Assert.Equal(altOut, stopped.StoppedAtUtc);

        DateTime altBackIn = pilotIn.AddMinutes(4);
        watch.Report(Alt, AbyssalRoom, altBackIn);
        Run resumed = await _WaitForStateAsync(window, instance, altBackIn, altRunId, StoredRunState.Running);

        Assert.Null(resumed.StoppedAtUtc);
        Assert.Equal(altSeenOutsideAtUtc, resumed.StartedAtUtc);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await db.Set<Run>().CountAsync());
    }

    /// <summary>Ticks the window until the alt's own row exists — its start is a fire-and-forget task off the tick
    /// that saw the crossing, so this settles both that task and the Participants read it hands off to.</summary>
    private static async Task<Guid> _WaitForOwnLegAsync(
        ActivityWindowViewModel window, TestClientInstance instance, DateTime nowUtc, Guid excludingRunId)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            window.Refresh(nowUtc);
            await using ClientDbContext db = await instance.Services
                .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
            Run? other = await db.Set<Run>().FirstOrDefaultAsync(run => run.Id != excludingRunId);
            if (other is not null)
                return other.Id;
            await Task.Delay(25);
        }

        throw new TimeoutException("the alt's own row never appeared");
    }

    /// <summary>Ticks the window until <paramref name="runId"/>'s own row reaches <paramref name="state"/> — the
    /// stop or resume this ticks off is itself a fire-and-forget task, same reason as <see cref="_WaitForOwnLegAsync"/>.</summary>
    private static async Task<Run> _WaitForStateAsync(
        ActivityWindowViewModel window, TestClientInstance instance, DateTime nowUtc, Guid runId, StoredRunState state)
    {
        Run? run = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            window.Refresh(nowUtc);
            await using ClientDbContext db = await instance.Services
                .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
            run = await db.Set<Run>().AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
            if (run.State == state)
                return run;
            await Task.Delay(25);
        }

        return run!;
    }

    /// <summary>Stands in for the ESI poll loop, per character, so a test can drive one toon's crossing without
    /// moving another's.</summary>
    private sealed class PerCharacterLocationWatch : IEsiLocationMonitor
    {
        private readonly Dictionary<int, Action<EsiLocationReading>> _readers = [];

        public void Watch(int characterId, string characterName, Action<EsiLocationReading> onReading) =>
            _readers[characterId] = onReading;

        public void UiReady() { }

        public void Stop(int characterId) => _readers.Remove(characterId);

        public bool IsWatching(int characterId) => _readers.ContainsKey(characterId);

        public void Report(int characterId, int solarSystemId, DateTime atUtc)
        {
            if (_readers.TryGetValue(characterId, out var onReading))
                onReading(new EsiLocationReading(solarSystemId, atUtc));
        }
    }
}
