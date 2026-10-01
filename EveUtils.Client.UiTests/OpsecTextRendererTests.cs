using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Opsec;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-417: a marked location is masked at the text edge — in any text block or run, alone or inside a composite
/// string — and turning OPSEC on or off redraws what is already on screen without the view model rebuilding anything.
/// Runs on the one hook this assembly installs (<see cref="TestOpsec"/>), the way App installs it for the client.
/// </summary>
public sealed partial class OpsecTextRendererTests
{
    private static readonly string Composite = $"Angel Hideaway · {OpsecText.Mark("Jita")} 0.9";

    [AvaloniaFact]
    public void MarkedText_OpsecOff_ShowsTheLocationWithoutMarkers()
    {
        var block = new TextBlock { Text = Composite };

        Assert.Equal("Angel Hideaway · Jita 0.9", block.Text);
    }

    [AvaloniaFact]
    public void MarkedText_OpsecOn_MasksOnlyTheLocation()
    {
        using IDisposable on = TestOpsec.On();

        var block = new TextBlock { Text = Composite };

        Assert.DoesNotContain("Jita", block.Text);
        Assert.StartsWith("Angel Hideaway · ", block.Text);
        Assert.EndsWith(" 0.9", block.Text);
        Assert.Equal(TestOpsec.Service.Render(Composite), block.Text);
    }

    [AvaloniaFact]
    public void Toggle_RedrawsTextAlreadyOnScreen_BothWays()
    {
        var block = new TextBlock { Text = Composite };

        using (TestOpsec.On())
            Assert.DoesNotContain("Jita", block.Text);

        Assert.Equal("Angel Hideaway · Jita 0.9", block.Text);

        using (TestOpsec.On())
            Assert.DoesNotContain("Jita", block.Text);
    }

    [AvaloniaFact]
    public void SourceChanges_WhileMasked_TheNewLocationIsMaskedToo()
    {
        var block = new TextBlock { Text = OpsecText.Mark("Jita") };

        using (TestOpsec.On())
        {
            block.Text = OpsecText.Mark("Amarr");
            Assert.Equal(TestOpsec.Service.Render(OpsecText.Mark("Amarr")), block.Text);
        }

        Assert.Equal("Amarr", block.Text);
    }

    [AvaloniaFact]
    public void BoundText_KeepsItsBinding_AcrossToggles()
    {
        var row = new LocationRow { Location = OpsecText.Mark("Jita") };
        var block = new TextBlock { DataContext = row };
        block.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(LocationRow.Location)));

        using (TestOpsec.On())
            Assert.Equal(TestOpsec.Service.Render(row.Location), block.Text);

        row.Location = OpsecText.Mark("Amarr");
        Assert.Equal("Amarr", block.Text);

        using (TestOpsec.On())
        {
            row.Location = OpsecText.Mark("Dodixie");
            Assert.Equal(TestOpsec.Service.Render(OpsecText.Mark("Dodixie")), block.Text);
        }
    }

    [AvaloniaFact]
    public void SourceLosesItsMarker_WhileMasked_ShowsThePlainText()
    {
        using IDisposable on = TestOpsec.On();
        var block = new TextBlock { Text = OpsecText.Mark("Jita") };

        block.Text = "offline";

        Assert.Equal("offline", block.Text);
    }

    [AvaloniaFact]
    public void UnmarkedText_IsNeverTouched()
    {
        var block = new TextBlock { Text = "Kirra Taxx" };

        using (TestOpsec.On())
            Assert.Equal("Kirra Taxx", block.Text);

        Assert.Equal("Kirra Taxx", block.Text);
    }

    [AvaloniaFact]
    public void MarkedRun_InsideATextBlock_IsMaskedLikeAnyText()
    {
        var run = new Run(OpsecText.Mark("Domain"));
        var block = new TextBlock();
        block.Inlines!.Add(new Run("Region: "));
        block.Inlines.Add(run);

        using (TestOpsec.On())
            Assert.Equal(TestOpsec.Service.Render(OpsecText.Mark("Domain")), run.Text);

        Assert.Equal("Domain", run.Text);
    }

    [AvaloniaFact]
    public void MarkConverter_MarksTheBoundValue_BeforeItsStringFormat()
    {
        var row = new LocationRow { Location = "Jita" };
        var block = new TextBlock { DataContext = row };
        block.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(LocationRow.Location))
        {
            Converter = OpsecMarkConverter.Instance,
            StringFormat = "Selected: {0}",
        });

        Assert.Equal("Selected: Jita", block.Text);
        using (TestOpsec.On())
            Assert.Equal($"Selected: {TestOpsec.Service.Render(OpsecText.Mark("Jita"))}", block.Text);
    }

    private sealed partial class LocationRow : ObservableObject
    {
        [ObservableProperty] private string _location = "";
    }
}
