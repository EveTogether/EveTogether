using System.Diagnostics;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The gamelog watcher tails files but must never replay history (B9): combat already in a file before <see cref="GameLogWatcher.Start"/>
/// is baselined at its current length and produces no <see cref="GameLogWatcher.EventParsed"/>, while lines appended after Start do fire.
/// </summary>
public sealed class GameLogWatcherTests : IDisposable
{
    private const string CharacterName = "Test Pilot";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eveutils-gamelog-" + Guid.NewGuid().ToString("N"));

    public GameLogWatcherTests() => Directory.CreateDirectory(_directory);

    private string LogPath() => Path.Combine(_directory, "20300101_120000_95123456.txt");

    private static string Header() =>
        "------------------------------------------------------------\n" +
        $"  Gamelog\n  Listener: {CharacterName}\n  Session Started: 2030.01.01 12:00:00\n" +
        "------------------------------------------------------------\n";

    private static string CombatLine(int amount, string target) =>
        $"[ 2030.01.01 12:00:0{amount % 10} ] (combat) {amount} to {target} - Light Ion Blaster II - Hits\n";

    [Fact]
    public async Task PreExistingHistory_IsNotReplayed_AfterStart()
    {
        var ct = TestContext.Current.CancellationToken;
        // A file with combat history already on disk before the watcher starts.
        await File.WriteAllTextAsync(LogPath(), Header() + CombatLine(100, "Angel Cartel Frigate"), ct);

        using var watcher = new GameLogWatcher(_directory, TimeSpan.FromMilliseconds(20));
        var events = new List<GameLogEvent>();
        watcher.EventParsed += (_, args) => { lock (events) events.Add(args.LogEvent); };

        watcher.Start();

        // Give several poll cycles a chance to (wrongly) replay the baselined history.
        await Task.Delay(200, ct);

        lock (events)
            Assert.Empty(events.OfType<CombatEvent>());
    }

    [Fact]
    public async Task LinesAppendedAfterStart_FireEventParsed()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(LogPath(), Header(), ct);

        using var watcher = new GameLogWatcher(_directory, TimeSpan.FromMilliseconds(20));
        var combat = new List<CombatEvent>();
        watcher.EventParsed += (_, args) =>
        {
            if (args.LogEvent is CombatEvent c)
                lock (combat) combat.Add(c);
        };

        watcher.Start();
        // Let the watcher baseline the header-only file first, then append a combat line as if the pilot started fighting.
        await Task.Delay(100, ct);
        await File.AppendAllTextAsync(LogPath(), CombatLine(250, "Guristas Destroyer"), ct);

        await WaitUntil(() => { lock (combat) return combat.Count > 0; }, ct);

        lock (combat)
        {
            var hit = Assert.Single(combat);
            Assert.Equal(250, hit.Amount);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken, int timeoutMs = 3000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
                return;
            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the scratch dir.
        }
    }
}
