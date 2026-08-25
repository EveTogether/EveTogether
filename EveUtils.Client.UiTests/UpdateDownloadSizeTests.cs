using System.Globalization;
using EveUtils.Client.Updates;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The download size shown in the offer. It sits beside English copy, so it must not pick up the machine's
/// decimal comma — this machine runs nl-NL and would otherwise render "78,0 MB" next to "Download and install".
/// </summary>
public class UpdateDownloadSizeTests
{
    [Fact]
    public void Format_RendersWholeMegabytes() =>
        Assert.Equal("78 MB", UpdateDownloadSize.Format(81_788_928));

    [Fact]
    public void Format_StaysInvariant_OnACommaDecimalMachine()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            Assert.DoesNotContain(",", UpdateDownloadSize.Format(81_788_928));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // A feed that reports no size must not be rendered as a size of zero — that reads as a fact rather than a gap.
    [Fact]
    public void Format_SaysUnknown_WhenTheFeedReportsNoSize() =>
        Assert.Equal("unknown", UpdateDownloadSize.Format(0));

    [Fact]
    public void Format_RoundsAnythingSmallerUpToOne() =>
        Assert.Equal("1 MB", UpdateDownloadSize.Format(4_096));
}
