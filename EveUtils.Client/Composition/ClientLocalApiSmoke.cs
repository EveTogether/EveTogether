using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.LocalApi;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless verification of the local API server against the <b>real</b> client services (via <c>--localapi-smoke</c>):
/// starts the host on a throwaway port, hits every endpoint + the OpenAPI/Scalar docs and prints status + size.
/// Read-only; never prints body content (could contain character data), only HTTP status and length.
/// </summary>
public static class ClientLocalApiSmoke
{
    public static async Task RunAsync(IServiceProvider services)
    {
        const int port = 8011; // throwaway, avoids clashing with a user-enabled 8001
        var server = services.GetRequiredService<ILocalApiServer>();
        await server.ApplyAsync(enabled: true, port);
        Console.WriteLine($"local API status: {server.Status.Status} on {server.Status.Url}");

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        foreach (var path in new[]
                 {
                     "/", "/docs", "/widget", "/api/v1/health", "/api/v1/metrics", "/api/v1/characters",
                     "/api/v1/fits", "/api/v1/fleets", "/api/v1/fleet", "/api/v1/compositions",
                     "/api/v1/types/587", "/openapi/v1.json", "/scalar/"
                 })
        {
            try
            {
                var response = await http.GetAsync(path, CancellationToken.None);
                var length = (await response.Content.ReadAsStringAsync()).Length;
                Console.WriteLine($"  GET {path} -> {(int)response.StatusCode} ({length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  GET {path} -> ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Probe the Dogma stats path against the real SDE: take the first fit and ask for ?stats=true.
        try
        {
            var listJson = await http.GetStringAsync("/api/v1/fits", CancellationToken.None);
            using var doc = System.Text.Json.JsonDocument.Parse(listJson);
            int computed = 0;
            double maxDps = 0;
            foreach (var fit in doc.RootElement.EnumerateArray())
            {
                var id = fit.GetProperty("id").GetInt32();
                var fitServer = fit.TryGetProperty("serverAddress", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.String
                    ? fs.GetString()
                    : null;
                var detailUrl = fitServer is null
                    ? $"/api/v1/fits/{id}?stats=true"
                    : $"/api/v1/fits/{id}?stats=true&server={Uri.EscapeDataString(fitServer)}";
                var detail = await http.GetStringAsync(detailUrl, CancellationToken.None);
                using var detailDoc = System.Text.Json.JsonDocument.Parse(detail);
                if (detailDoc.RootElement.TryGetProperty("stats", out var stats) && stats.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    computed++;
                    maxDps = Math.Max(maxDps, stats.GetProperty("totalDps").GetDouble());
                }
            }
            Console.WriteLine($"  stats: {computed}/{doc.RootElement.GetArrayLength()} fits computed, max totalDps={maxDps:F1}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  stats probe -> ERROR {ex.GetType().Name}: {ex.Message}");
        }

        // fleets: count the listed fleets (local + per-server) without printing names.
        try
        {
            var fleetsJson = await http.GetStringAsync("/api/v1/fleets", CancellationToken.None);
            using var doc = System.Text.Json.JsonDocument.Parse(fleetsJson);
            Console.WriteLine($"  fleets: {doc.RootElement.GetArrayLength()} listed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  fleets probe -> ERROR {ex.GetType().Name}: {ex.Message}");
        }

        // compositions: resolve each listed composition's detail tree (local via id, server via ?server=).
        try
        {
            var compsJson = await http.GetStringAsync("/api/v1/compositions", CancellationToken.None);
            using var doc = System.Text.Json.JsonDocument.Parse(compsJson);
            int resolved = 0;
            foreach (var comp in doc.RootElement.EnumerateArray())
            {
                var id = comp.GetProperty("id").GetInt64();
                var compServer = comp.TryGetProperty("serverAddress", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                    ? s.GetString()
                    : null;
                var url = compServer is null
                    ? $"/api/v1/compositions/{id}"
                    : $"/api/v1/compositions/{id}?server={Uri.EscapeDataString(compServer)}";
                var response = await http.GetAsync(url, CancellationToken.None);
                if (response.IsSuccessStatusCode) resolved++;
            }
            Console.WriteLine($"  compositions: {resolved}/{doc.RootElement.GetArrayLength()} details resolved");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  compositions probe -> ERROR {ex.GetType().Name}: {ex.Message}");
        }

        // realtime: connect to /ws, read the snapshot envelope + the first live tick, print only types + sizes.
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
            var snapshot = await ReceiveTextAsync(ws);
            Console.WriteLine($"  ws: connected, snapshot={EventType(snapshot)} ({snapshot.Length} bytes)");
            using var tickCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var tick = await ReceiveTextAsync(ws, tickCts.Token);
            Console.WriteLine($"  ws: next event={EventType(tick)} ({tick.Length} bytes)");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ws probe -> ERROR {ex.GetType().Name}: {ex.Message}");
        }

        // SignalR: the negotiate handshake proves the hub is mapped (a full client needs the SignalR client package).
        try
        {
            var response = await http.PostAsync("/hub/fleet/negotiate?negotiateVersion=1", content: null, CancellationToken.None);
            var ok = (await response.Content.ReadAsStringAsync()).Contains("connectionId");
            Console.WriteLine($"  hub: negotiate -> {(int)response.StatusCode} (connectionId={(ok ? "yes" : "no")})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  hub probe -> ERROR {ex.GetType().Name}: {ex.Message}");
        }

        await server.StopAsync();
    }

    private static string EventType(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
        }
        catch { return "?"; }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);
        return builder.ToString();
    }
}
