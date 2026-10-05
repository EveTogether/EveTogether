using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Opsec;
using EveUtils.Shared.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-417: the location markers are for the screen only and never leave it. Pasting one into EVE, writing one to a log
/// file, or letting one change a search or a sort order would each be a bug of its own.
/// </summary>
public sealed class OpsecMarkerBoundaryTests
{
    private static readonly string Marked = $"In {OpsecText.Mark("Jita")} — with the FC";

    [AvaloniaFact]
    public async Task Clipboard_GetsThePlainText_EvenWithOpsecOn()
    {
        var window = new Window();
        window.Show();
        var dialogs = new DialogService();
        dialogs.SetOwner(window);

        using (TestOpsec.On())
            await dialogs.SetClipboardTextAsync(Marked);

        Assert.Equal("In Jita — with the FC", await dialogs.GetClipboardTextAsync());
        window.Close();
    }

    [Fact]
    public void LogFile_GetsThePlainText_WhileTheLogsScreenKeepsTheMarkers()
    {
        string directory = Directory.CreateTempSubdirectory("eveutils-opsec-log-").FullName;
        try
        {
            var store = new InMemoryLogStore(dataDirectory: directory);

            store.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "Homefront", Marked, null));

            string written = File.ReadAllText(Path.Combine(directory, "app-errors.jsonl"));
            Assert.DoesNotContain("\\uFFF9", written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\uFFFB", written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(OpsecText.Open, written);
            Assert.Contains("In Jita", written);
            Assert.Equal(Marked, store.GetAll().Single().Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sort_MarkedLocations_KeepTheirPlainOrder()
    {
        string[] plain = ["Rens", "Amarr", "jita", "Dodixie", "Hek"];

        IEnumerable<string> sortedMarked = plain.Select(name => OpsecText.Mark(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => OpsecText.Strip(name));

        Assert.Equal(plain.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), sortedMarked);
    }

    [AvaloniaFact]
    public void Trimming_MeasuresTheTextOnScreen_NotTheMarkers()
    {
        var block = new TextBlock { Text = Marked, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };

        Assert.Equal("In Jita — with the FC".Length, block.Text?.Length);
    }
}
