using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Gamelog;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.GameLogs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-410: the GAME LOGS screen over a fixture folder of two characters in two languages (Alpha Pilot in English, Bravo
/// Pilot in German — the German header is SYNTHETIC like ET-278's; the category words that classify a line are the same
/// in every language). Every test asserts counts, never a render.
/// </summary>
public sealed class GameLogsTests : IDisposable
{
    private const string Alpha = "Alpha Pilot";
    private const string Bravo = "Bravo Pilot";
    private const string Rule = "------------------------------------------------------------\n";

    private static readonly DateTime Now = new(2030, 1, 1, 13, 0, 0, DateTimeKind.Utc);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eveutils-gamelogs-" + Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _disposables = [];

    public GameLogsTests() => Directory.CreateDirectory(_directory);

    private string AlphaPath => Path.Combine(_directory, "20300101_100000_95000001.txt");

    private string BravoPath => Path.Combine(_directory, "20300101_110000_95000002.txt");

    private static string Header(string listener, string sessionStarted, string name, string started) =>
        Rule + $"  Gamelog\n  {listener}: {name}\n  {sessionStarted}: {started}\n" + Rule;

    private static string Line(string time, string category, string text, string day = "2030.01.01") =>
        $"[ {day} {time} ] ({category}) {text}\n";

    /// <summary>Alpha: 6 combat, 3 mining, 2 travel, 1 notify, 1 info, 2 hint (hint + question), 2 bounty = 17 today;
    /// one line from the day before and one line without the game's framing, neither of which belongs on today's list.</summary>
    private async Task WriteFixtureAsync()
    {
        StringBuilder alpha = new(Header("Listener", "Session Started", Alpha, "2030.01.01 10:00:00"));
        alpha.Append(Line("23:59:00", "combat", "50 from Old Pirate - Hits", "2029.12.31"));
        alpha.Append("a line EVE wrapped without its frame\n");
        for (int i = 0; i < 6; i++)
            alpha.Append(Line($"10:00:{10 + i:00}", "combat", $"{100 + i} to Serpentis Frigate - Light Ion Blaster II - Hits"));
        for (int i = 0; i < 3; i++)
            alpha.Append(Line($"10:01:{10 + i:00}", "mining", $"You mined {100 + i} units of Veldspar"));
        alpha.Append(Line("10:02:00", "None", "Jumping from Jita to Perimeter"));
        alpha.Append(Line("10:02:30", "None", "Undocking from Jita IV to Jita solar system."));
        alpha.Append(Line("10:03:00", "notify", "Your drone is out of range"));
        alpha.Append(Line("10:03:30", "info", "You have no ship fitted"));
        alpha.Append(Line("10:04:00", "hint", "Use the overview to select a target"));
        alpha.Append(Line("10:04:30", "question", "Do you want to undock?"));
        alpha.Append(Line("10:05:00", "bounty", "1,000 ISK added to next bounty payout"));
        alpha.Append(Line("10:05:30", "bounty", "2,000 ISK added to next bounty payout"));
        await File.WriteAllTextAsync(AlphaPath, alpha.ToString(), Ct);

        StringBuilder bravo = new(Header("Empfänger", "Sitzung gestartet", Bravo, "2030.01.01 11:00:00"));
        for (int i = 0; i < 4; i++)
            bravo.Append(Line($"11:00:{20 + i:00}", "combat", $"{200 + i} zu Guristas Fregatte - Leichte Ionenkanone II - Treffer"));
        for (int i = 0; i < 2; i++)
            bravo.Append(Line($"11:01:{20 + i:00}", "mining", $"Sie haben {50 + i} Einheiten Veldspar abgebaut"));
        bravo.Append(Line("11:02:00", "None", "Springe von Amarr nach Ashab"));
        bravo.Append(Line("11:03:00", "notify", "Ihre Drohne ist außer Reichweite"));
        bravo.Append(Line("11:03:30", "notify", "Ihr Schiff wurde gescannt"));
        bravo.Append(Line("11:05:00", "bounty", "3.000 ISK zur nächsten Kopfgeldzahlung hinzugefügt"));
        await File.WriteAllTextAsync(BravoPath, bravo.ToString(), Ct);
        File.SetLastWriteTimeUtc(AlphaPath, Now);
        File.SetLastWriteTimeUtc(BravoPath, Now);
    }

    // The fixture lives in 2030, so the files carry that write time — the reader skips files nobody wrote to since the period began.
    private async Task AppendAsync(string path, string line)
    {
        await File.AppendAllTextAsync(path, line, Ct);
        File.SetLastWriteTimeUtc(path, Now);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (GameLogsViewModel ViewModel, GameLogWatcher Watcher) Open(params Character[] linked)
    {
        GameLogWatcher watcher = new(_directory, TimeSpan.FromMilliseconds(20));
        watcher.Start();
        GameLogLineBuffer buffer = new(watcher);
        _disposables.Add(watcher);
        _disposables.Add(buffer);

        GameLogsViewModel viewModel = new(new FixedSource(buffer), linked, portraits: null, new FixedClock(Now));
        _disposables.Add(viewModel);
        return (viewModel, watcher);
    }

    /// <summary>Criterion: the default view is today, every character, chronological, with the numbers the fixture has.</summary>
    [AvaloniaFact]
    public async Task Default_ShowsTodaysLinesOfEveryCharacter_Chronologically_WithTheCountsPerCharacterAndType()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();

        await viewModel.LoadAsync();

        Assert.Equal(27, viewModel.Rows.Count);
        Assert.Equal(viewModel.Rows.OrderBy(row => row.Timestamp), viewModel.Rows);
        Assert.Equal(["All characters", Alpha, Bravo], viewModel.CharacterOptions.Select(option => option.Label));
        Assert.Equal([27, 17, 10], viewModel.CharacterOptions.Select(option => option.Count));
        Assert.Equal(
            new Dictionary<GameLogLineKind, int>
            {
                [GameLogLineKind.Combat] = 10, [GameLogLineKind.Mining] = 5, [GameLogLineKind.Travel] = 3,
                [GameLogLineKind.Notify] = 3, [GameLogLineKind.Info] = 1, [GameLogLineKind.Hint] = 2,
                [GameLogLineKind.Bounty] = 3, [GameLogLineKind.Other] = 0
            },
            viewModel.KindChips.ToDictionary(chip => chip.Kind, chip => chip.Count));
        Assert.All(viewModel.KindChips, chip => Assert.True(chip.IsOn));
        Assert.True(viewModel.CharacterOptions[0].IsAll);
        Assert.Same(viewModel.CharacterOptions[0], viewModel.SelectedCharacter);
    }

    /// <summary>Criterion: one character gives exactly that character's lines, and the type chips work with it.</summary>
    [AvaloniaFact]
    public async Task CharacterFilter_GivesExactlyThatCharactersLines_AndWorksWithTheTypeChips()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();

        viewModel.SelectedCharacter = viewModel.CharacterOptions.Single(option => option.Name == Bravo);
        Assert.Equal(10, viewModel.Rows.Count);
        Assert.All(viewModel.Rows, row => Assert.Equal(Bravo, row.CharacterName));

        viewModel.KindChips.Single(chip => chip.Kind == GameLogLineKind.Combat).ToggleCommand.Execute(null);
        Assert.Equal(6, viewModel.Rows.Count);
        Assert.DoesNotContain(viewModel.Rows, row => row.Kind == GameLogLineKind.Combat);
        Assert.Equal(4, viewModel.KindChips.Single(chip => chip.Kind == GameLogLineKind.Combat).Count);

        viewModel.SelectedCharacter = viewModel.CharacterOptions[0];
        Assert.Equal(17, viewModel.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Search_MatchesTheTextAndThePilotName()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();

        viewModel.SearchText = "serpentis";
        Assert.Equal(6, viewModel.Rows.Count);

        viewModel.SearchText = "bravo";
        Assert.Equal(10, viewModel.Rows.Count);

        viewModel.SearchText = "no such thing";
        Assert.Empty(viewModel.Rows);
    }

    /// <summary>A line is typed by the game's own category, so the German log classifies like the English one.</summary>
    [AvaloniaFact]
    public async Task GermanLog_IsClassifiedLikeTheEnglishOne()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();

        viewModel.SelectedCharacter = viewModel.CharacterOptions.Single(option => option.Name == Bravo);

        Assert.Equal(
            [GameLogLineKind.Combat, GameLogLineKind.Mining, GameLogLineKind.Travel, GameLogLineKind.Notify, GameLogLineKind.Bounty],
            viewModel.Rows.Select(row => row.Kind).Distinct());
        Assert.Equal(OpsecText.Mark("Springe von Amarr nach Ashab"), viewModel.Rows.Single(row => row.Kind == GameLogLineKind.Travel).Text);
    }

    [AvaloniaFact]
    public async Task Period_Last7Days_ReachesTheDayBefore_AndAPickedDateShowsOnlyThatDay()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();
        Assert.Equal(27, viewModel.Rows.Count);

        viewModel.SelectedPeriod = viewModel.PeriodOptions.Single(option => option.Period == GameLogPeriod.Last7Days);
        await _WaitForAsync(() => viewModel.Rows.Count == 28);
        Assert.Equal(28, viewModel.Rows.Count);

        viewModel.SelectedPeriod = viewModel.PeriodOptions.Single(option => option.Period == GameLogPeriod.PickDate);
        viewModel.PickedDate = new DateTime(2029, 12, 31);
        await _WaitForAsync(() => viewModel.Rows.Count == 1);
        Assert.Equal("50 from Old Pirate - Hits", Assert.Single(viewModel.Rows).Text);
    }

    /// <summary>Criterion: a line appended to an active log shows up, in its place, without a reload.</summary>
    [AvaloniaFact]
    public async Task Tail_ALineAppendedToAnActiveLog_AppearsWithoutReloading()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();
        Assert.Equal(27, viewModel.Rows.Count);

        await AppendAsync(AlphaPath, Line("12:30:00", "combat", "777 to Tail Target - Light Ion Blaster II - Hits"));

        Assert.True(await _WaitForAsync(() => viewModel.Rows.Any(row => row.Text.StartsWith("777 to Tail Target", StringComparison.Ordinal))));
        GameLogRowViewModel tailed = viewModel.Rows.Single(row => row.Text.StartsWith("777 to Tail Target", StringComparison.Ordinal));
        Assert.Equal((Alpha, GameLogLineKind.Combat, 28), (tailed.CharacterName, tailed.Kind, viewModel.Rows.Count));
        Assert.Same(tailed, viewModel.Rows[^1]);
        Assert.Equal(11, viewModel.KindChips.Single(chip => chip.Kind == GameLogLineKind.Combat).Count);
        Assert.Equal(18, viewModel.CharacterOptions.Single(option => option.Name == Alpha).Count);

        // A read of the same period again must not count the tailed line twice: once from the file, once from the tail.
        await viewModel.LoadAsync();
        Assert.Equal(28, viewModel.Rows.Count);
    }

    /// <summary>A tailed line from another character that is older than the newest line lands in time order, not last.</summary>
    [AvaloniaFact]
    public async Task Tail_ALineOlderThanTheNewest_IsInsertedByTime()
    {
        await WriteFixtureAsync();
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();

        await AppendAsync(BravoPath, Line("10:00:00", "notify", "Late arrival"));

        Assert.True(await _WaitForAsync(() => viewModel.Rows.Any(row => row.Text == OpsecText.Mark("Late arrival"))));
        Assert.Equal(viewModel.Rows.OrderBy(row => row.Timestamp), viewModel.Rows);
        Assert.Equal(OpsecText.Mark("Late arrival"), viewModel.Rows[0].Text);
    }

    /// <summary>A line the watcher read before the screen opened is in the history and arrives live too: it must show once.</summary>
    [AvaloniaFact]
    public async Task ALineReadBeforeTheScreenOpened_IsShownOnce()
    {
        await WriteFixtureAsync();
        GameLogWatcher watcher = new(_directory, TimeSpan.FromMilliseconds(20));
        _disposables.Add(watcher);
        int batches = 0;
        watcher.LinesRead += (_, _) => Interlocked.Increment(ref batches);
        watcher.Start();
        await AppendAsync(AlphaPath, Line("12:00:00", "notify", "Written before the screen opened"));
        Assert.True(await _WaitForAsync(() => Volatile.Read(ref batches) > 0));

        GameLogLineBuffer buffer = new(watcher);
        _disposables.Add(buffer);
        GameLogsViewModel viewModel = new(new FixedSource(buffer), [], portraits: null, new FixedClock(Now));
        _disposables.Add(viewModel);
        await viewModel.LoadAsync();
        viewModel.FlushPendingNow();

        Assert.Equal(1, viewModel.Rows.Count(row => row.Text == OpsecText.Mark("Written before the screen opened")));
        Assert.Equal(28, viewModel.Rows.Count);
    }

    /// <summary>EVE keeps writing while the screen reads: the read never locks, writes or moves the file.</summary>
    [AvaloniaFact]
    public async Task Reading_LeavesTheFilesAsTheyWere_AndWorksWhileEveHoldsThemOpen()
    {
        await WriteFixtureAsync();
        byte[] before = await File.ReadAllBytesAsync(AlphaPath, Ct);
        byte[] appended = Encoding.UTF8.GetBytes(Line("12:45:00", "notify", "EVE is still writing"));
        using (FileStream eve = new(AlphaPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            (GameLogsViewModel viewModel, _) = Open();

            await viewModel.LoadAsync();
            eve.Write(appended, 0, appended.Length);
            eve.Flush();

            Assert.Equal(27, viewModel.Rows.Count);
            Assert.True(await _WaitForAsync(() => viewModel.Rows.Any(row => row.Text == OpsecText.Mark("EVE is still writing"))));
        }

        byte[] after = await File.ReadAllBytesAsync(AlphaPath, Ct);
        Assert.Equal(before.Concat(appended), after);
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    /// <summary>Criterion: a day of combat stays smooth — 20,000 lines are all in the list, but only the visible rows exist.</summary>
    [AvaloniaFact]
    public async Task TwentyThousandLines_AreVirtualized_OnlyTheVisibleRowsAreMaterialized()
    {
        StringBuilder alpha = new(Header("Listener", "Session Started", Alpha, "2030.01.01 00:00:00"));
        for (int i = 0; i < 20_000; i++)
        {
            TimeSpan at = TimeSpan.FromSeconds(i * 4);
            alpha.Append(Line($"{at.Hours:00}:{at.Minutes:00}:{at.Seconds:00}", "combat", $"{100 + i % 50} to Rat {i} - Light Ion Blaster II - Hits"));
        }

        await File.WriteAllTextAsync(AlphaPath, alpha.ToString(), Ct);
        File.SetLastWriteTimeUtc(AlphaPath, Now);
        (GameLogsViewModel viewModel, _) = Open();
        await viewModel.LoadAsync();
        GameLogsWindow window = new(viewModel) { Width = 980, Height = 640 };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name == "LinesList");
        int materialized = list.ItemsPanelRoot!.Children.Count;
        Assert.Equal(20_000, viewModel.Rows.Count);
        Assert.InRange(materialized, 1, 100);
        window.Close();
    }

    /// <summary>Criterion: the type colours are told apart and readable on the panel they sit on.</summary>
    [Fact]
    public void Palette_GivesEveryTypeItsOwnColour_ThatIsReadableOnThePanel()
    {
        GameLogLineKind[] kinds = Enum.GetValues<GameLogLineKind>();

        Assert.Equal(kinds.Length, GameLogKindPalette.Colors.Values.Distinct().Count());
        foreach (GameLogLineKind kind in kinds)
            Assert.True(_Contrast(GameLogKindPalette.Colors[kind], GameLogKindPalette.Background) >= 4.5, $"{kind} is too dim");
    }

    private static double _Contrast(Color foreground, Color background)
    {
        double a = _Luminance(foreground), b = _Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double _Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double scaled = value / 255.0;
            return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static async Task<bool> _WaitForAsync(Func<bool> condition, int tries = 200)
    {
        for (int i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return false;
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in _disposables)
            disposable.Dispose();

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedSource(GameLogLineBuffer buffer) : IGameLogLineSource
    {
        public GameLogLineBuffer? CurrentBuffer { get; } = buffer;

        public event Action? BufferReplaced
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
