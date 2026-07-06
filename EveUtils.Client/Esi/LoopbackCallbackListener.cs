using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EveUtils.Client.Esi;

/// <summary>
/// One-shot loopback HTTP listener for the EVE SSO redirect (Mode A / Tier-0). Binds the
/// fixed <c>127.0.0.1</c> callback registered in the EVE app; loopback http needs no TLS/cert.
/// </summary>
public sealed class LoopbackCallbackListener(string callbackUri)
{
    public async Task<CallbackResult> WaitForCallbackAsync(CancellationToken cancellationToken = default)
    {
        var prefix = callbackUri.EndsWith('/') ? callbackUri : callbackUri + "/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var query = context.Request.QueryString;
            var result = new CallbackResult(query["code"], query["state"], query["error"]);

            await WriteClosePageAsync(context.Response, cancellationToken);
            return result;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteClosePageAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        const string html =
            "<!doctype html><html><body style=\"font-family:sans-serif;background:#06070a;color:#d8d0c4;" +
            "display:flex;align-items:center;justify-content:center;height:100vh\">" +
            "<div style=\"text-align:center\"><h2 style=\"color:#7ee0bb\">EVE Together — sign-in complete</h2>" +
            "<p>You can close this tab and return to the app.</p></div></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }
}
