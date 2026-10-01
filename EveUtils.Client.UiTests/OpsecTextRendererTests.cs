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
/// </summary>
public sealed partial class OpsecTextRendererTests
{
    private static readonly string Composite = $"Angel Hideaway · {OpsecText.Mark("Jita")} 0.9";

    [AvaloniaFact]
    public void MarkedText_OpsecOff_ShowsTheLocationWithoutMarkers()
    {
        var opsec = new FakeOpsecService();
        using var renderer = new OpsecTextRenderer(opsec);

        var block = new TextBlock { Text = Composite };

        Assert.Equal("Angel Hideaway · Jita 0.9", block.Text);
    }

    [AvaloniaFact]
    public void MarkedText_OpsecOn_MasksOnlyTheLocation()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);

        var block = new TextBlock { Text = Composite };

        Assert.DoesNotContain("Jita", block.Text);
        Assert.StartsWith("Angel Hideaway · ", block.Text);
        Assert.EndsWith(" 0.9", block.Text);
        Assert.Equal(opsec.Render(Composite), block.Text);
    }

    [AvaloniaFact]
    public void Toggle_RedrawsTextAlreadyOnScreen_BothWays()
    {
        var opsec = new FakeOpsecService();
        using var renderer = new OpsecTextRenderer(opsec);
        var block = new TextBlock { Text = Composite };

        opsec.Set(true);
        Assert.DoesNotContain("Jita", block.Text);

        opsec.Set(false);
        Assert.Equal("Angel Hideaway · Jita 0.9", block.Text);

        opsec.Set(true);
        Assert.DoesNotContain("Jita", block.Text);
    }

    [AvaloniaFact]
    public void SourceChanges_WhileMasked_TheNewLocationIsMaskedToo()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);
        var block = new TextBlock { Text = OpsecText.Mark("Jita") };

        block.Text = OpsecText.Mark("Amarr");

        Assert.Equal(opsec.Render(OpsecText.Mark("Amarr")), block.Text);
        opsec.Set(false);
        Assert.Equal("Amarr", block.Text);
    }

    [AvaloniaFact]
    public void BoundText_KeepsItsBinding_AcrossToggles()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);
        var row = new LocationRow { Location = OpsecText.Mark("Jita") };
        var block = new TextBlock { DataContext = row };
        block.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(LocationRow.Location)));

        Assert.Equal(opsec.Render(row.Location), block.Text);

        opsec.Set(false);
        row.Location = OpsecText.Mark("Amarr");
        Assert.Equal("Amarr", block.Text);

        opsec.Set(true);
        row.Location = OpsecText.Mark("Dodixie");
        Assert.Equal(opsec.Render(OpsecText.Mark("Dodixie")), block.Text);
    }

    [AvaloniaFact]
    public void SourceLosesItsMarker_WhileMasked_ShowsThePlainText()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);
        var block = new TextBlock { Text = OpsecText.Mark("Jita") };

        block.Text = "offline";

        Assert.Equal("offline", block.Text);
    }

    [AvaloniaFact]
    public void UnmarkedText_IsNeverTouched()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);
        var block = new TextBlock { Text = "Kirra Taxx" };

        opsec.Set(false);
        opsec.Set(true);

        Assert.Equal("Kirra Taxx", block.Text);
    }

    [AvaloniaFact]
    public void MarkedRun_InsideATextBlock_IsMaskedLikeAnyText()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        using var renderer = new OpsecTextRenderer(opsec);
        var run = new Run(OpsecText.Mark("Domain"));
        var block = new TextBlock();
        block.Inlines!.Add(new Run("Region: "));
        block.Inlines.Add(run);

        Assert.Equal(opsec.Render(OpsecText.Mark("Domain")), run.Text);
        opsec.Set(false);
        Assert.Equal("Domain", run.Text);
    }

    [AvaloniaFact]
    public void Disposed_StopsRenderingNewText()
    {
        var opsec = new FakeOpsecService { IsEnabled = true };
        new OpsecTextRenderer(opsec).Dispose();

        var block = new TextBlock { Text = OpsecText.Mark("Jita") };

        Assert.Equal(OpsecText.Mark("Jita"), block.Text);
    }

    private sealed partial class LocationRow : ObservableObject
    {
        [ObservableProperty] private string _location = "";
    }
}
