using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The pairing-callback page HTML-encodes every externally-sourced value (A1): a character/server name carrying markup
/// such as &lt;script&gt; must come back encoded so it cannot inject script into the reflected page.
/// </summary>
public class PairingCallbackPageTests
{
    [Fact]
    public void Success_CharacterNameWithScript_IsHtmlEncoded()
    {
        var page = PairingCallbackPage.Success("My Server", "<script>alert(1)</script>", "Pilots Inc");

        Assert.DoesNotContain("<script>alert(1)</script>", page);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
    }

    [Fact]
    public void Success_ServerNameWithMarkup_IsHtmlEncoded()
    {
        var page = PairingCallbackPage.Success("<b>Evil</b>", "Honest Pilot", "Pilots Inc");

        Assert.DoesNotContain("<b>Evil</b>", page);
        Assert.Contains("&lt;b&gt;Evil&lt;/b&gt;", page);
    }

    [Fact]
    public void Success_AffiliationWithMarkup_IsHtmlEncoded()
    {
        var page = PairingCallbackPage.Success("My Server", "Honest Pilot", "<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page);
    }

    [Fact]
    public void Success_PlainValues_AppearVerbatim()
    {
        var page = PairingCallbackPage.Success("My Server", "Honest Pilot", "Pilots Inc");

        Assert.Contains("My Server", page);
        Assert.Contains("Honest Pilot", page);
        Assert.Contains("Pilots Inc", page);
    }
}
