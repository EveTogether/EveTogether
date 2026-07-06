using System.Net;

namespace EveUtils.Server;

/// <summary>
/// Renders the SSO pairing-callback HTML page (A1). All externally-sourced values (server name, character name, ESI
/// corp/alliance) are HTML-encoded before interpolation so they cannot inject markup — the reflected-XSS guard on the
/// pairing-callback page.
/// </summary>
internal static class PairingCallbackPage
{
    public static string Render(string heading, string body) =>
        "<!doctype html><html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;background:#06070a;color:#d8d0c4;" +
        "display:flex;align-items:center;justify-content:center;height:100vh\"><div style=\"text-align:center\">" +
        $"<h2 style=\"color:#7ee0bb\">{heading}</h2>{body}</div></body></html>";

    /// <summary>The success page for a completed pairing. Encodes every interpolated value to avoid reflected XSS.</summary>
    public static string Success(string? serverName, string? characterName, string? affiliation)
    {
        var encodedServerName = WebUtility.HtmlEncode(serverName);
        var encodedCharacterName = WebUtility.HtmlEncode(characterName);
        var encodedAffiliation = WebUtility.HtmlEncode(affiliation);
        var body =
            $"<p>You are connected to <b style=\"color:#9be8c9\">{encodedServerName}</b>.</p>" +
            $"<p>Authorized as <b style=\"color:#f3ece0\">{encodedCharacterName}</b><br>" +
            $"<span style=\"color:#8a7e6b\">{encodedAffiliation}</span></p>" +
            "<p style=\"color:#8a7e6b\">You can close this tab and return to the app.</p>";
        return Render($"Connected to {encodedServerName} ✓", body);
    }
}
