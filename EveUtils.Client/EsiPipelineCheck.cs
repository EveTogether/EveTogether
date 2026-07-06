using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EveUtils.Client.Esi.Testing;
using EveUtils.Shared.App;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using static EveUtils.Client.Esi.Testing.EsiTestResponses;

namespace EveUtils.Client;

/// <summary>
/// Headless proof that every ESI fallback works (runnable via <c>--esi-test</c>). Drives the real
/// pivot + handler chain (header → cache → rate-limit → retry) over a scripted stub handler, covering the
/// 16-scenario checklist plus the pre-flight scope/token gate. Exit 0 = all pass, 1 = a failure.
/// </summary>
public static class EsiPipelineCheck
{
    private const string ProbeBody = "{\"value\":42}";
    private static readonly string Base = EsiEndpoints.PublicDataBaseUrl;

    private sealed record Probe(int Value);

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== EVE Together ESI pipeline check (pivot + handler chain fallbacks) ==");
        var failures = 0;

        void Check(string name, bool ok)
        {
            Console.WriteLine($"  {(ok ? "✓" : "✗")}  {name}");
            if (!ok) failures++;
        }

        // ── Pre-flight (scope + token) ──────────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, EsiAuthorization.ScopeMissing("esi-x"));
            var r = await client.GetAsync<Probe>("/characters/5/x/", characterId: 5, requiredScopes: ["esi-x"]);
            Check("P1 scope missing → no call, SCOPE_MISSING", !r.IsSuccess && r.Error!.Kind == EsiErrorKind.ScopeMissing && stub.Calls == 0);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, EsiAuthorization.AuthRequired);
            var r = await client.GetAsync<Probe>("/characters/5/x/", characterId: 5, requiredScopes: ["esi-x"]);
            Check("P2 auth required → no call, AUTH_REQUIRED(401)", !r.IsSuccess && r.Error!.Kind == EsiErrorKind.AuthRequired && r.Error.HttpStatus == 401 && stub.Calls == 0);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, EsiAuthorization.Authorized("tok123"));
            var r = await client.GetAsync<Probe>("/characters/5/x/", characterId: 5, requiredScopes: ["esi-x"]);
            Check("P3 authorized → bearer attached + success", r.IsSuccess && r.Value!.Value == 42 && stub.Last.Authorization == "Bearer tok123");
        }

        // ── Headers (13) ────────────────────────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/status/");
            Check("13 headers present (User-Agent + X-Compatibility-Date + Accept)",
                r.IsSuccess && stub.Last.UserAgent == AppInfo.UserAgent(ExecutionHost.Client)
                && stub.Last.CompatibilityDate == EsiEndpoints.CompatibilityDate
                && stub.Last.Accept.Contains("application/json"));
        }

        // ── Caching (1, 2, 3, 4, 14) ────────────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody).WithExpires(TimeSpan.FromSeconds(300)));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r1 = await client.GetAsync<Probe>("/markets/prices/");
            var r2 = await client.GetAsync<Probe>("/markets/prices/");
            Check("1 fresh Expires-TTL → 2nd served from cache, no 2nd socket call",
                r1.IsSuccess && r2.IsSuccess && stub.Calls == 1 && r2.FromCache && !r1.FromCache);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody)); // no Expires
            var (client, _, store) = Wire(stub, AnyAuth);
            await client.GetAsync<Probe>("/x2/");
            var entry = await store.GetAsync(FileEsiCacheStore.KeyFor($"{Base}/x2/"));
            var ttl = entry!.ExpiresAt!.Value - DateTimeOffset.UtcNow;
            Check("2 no Expires → ~1h fallback TTL (±10% jitter)", ttl > TimeSpan.FromMinutes(54) && ttl < TimeSpan.FromMinutes(66));
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(304, ""));
            var (client, _, store) = Wire(stub, AnyAuth);
            var key = FileEsiCacheStore.KeyFor($"{Base}/seed/");
            await store.SetAsync(key, new EsiCacheEntry(ProbeBody, "\"etag1\"", DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-10)));
            var r = await client.GetAsync<Probe>("/seed/");
            Check("3 stale + ETag → If-None-Match sent, 304 reuses cached body",
                r.IsSuccess && r.Value!.Value == 42 && r.FromCache && stub.Calls == 1 && stub.Last.IfNoneMatch?.Contains("etag1") == true);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody).WithExpires(TimeSpan.FromHours(1)));
            var (client, _, store) = Wire(stub, AnyAuth);
            await client.GetAsync<Probe>("/a/");
            await client.GetAsync<Probe>("/b/");
            var ea = (await store.GetAsync(FileEsiCacheStore.KeyFor($"{Base}/a/")))!.ExpiresAt!.Value;
            var eb = (await store.GetAsync(FileEsiCacheStore.KeyFor($"{Base}/b/")))!.ExpiresAt!.Value;
            Check("4 jitter → two same-Expires entries expire at different times", ea != eb);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody)); // killmail, no Expires
            var (client, _, store) = Wire(stub, AnyAuth);
            await client.GetAsync<Probe>("/killmails/1/abc/");
            var r2 = await client.GetAsync<Probe>("/killmails/1/abc/");
            var entry = await store.GetAsync(FileEsiCacheStore.KeyFor($"{Base}/killmails/1/abc/"));
            Check("14 immutable killmail → forever cache, no 2nd call", entry!.ExpiresAt is null && stub.Calls == 1 && r2.FromCache);
        }

        // ── Rate limiting (6, 11, 15) ───────────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(420, "{\"error\":\"limited\"}").WithErrorLimit(0, 30));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r1 = await client.GetAsync<Probe>("/limited/");
            var r2 = await client.GetAsync<Probe>("/limited/");
            Check("6 420 → stop (no retry) + gate short-circuits the 2nd call",
                !r1.IsSuccess && r1.Error!.RateLimitKind == EsiRateLimitKind.ErrorLimit && stub.Calls == 1
                && !r2.IsSuccess && r2.Error!.RateLimitKind == EsiRateLimitKind.ErrorLimit);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody).WithErrorLimit(5, 1));
            var (client, _, _) = Wire(stub, AnyAuth);
            await client.GetAsync<Probe>("/warm/");
            var sw = Stopwatch.StartNew();
            var r2 = await client.GetAsync<Probe>("/warm2/");
            sw.Stop();
            Check("11 error-limit low → pre-emptive throttle (waited, still sent)",
                r2.IsSuccess && stub.Calls == 2 && sw.ElapsedMilliseconds >= 120);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, ProbeBody).WithBucket("g", 100, 50, 50));
            var (client, monitor, _) = Wire(stub, EsiAuthorization.Authorized("t"));
            await client.GetAsync<Probe>("/auth/", characterId: 7, requiredScopes: []);
            await client.GetAsync<Probe>("/pub/");
            var keys = monitor.Buckets.Select(b => b.Key).ToHashSet();
            Check("15 bucket separation app:character vs ip", keys.Contains("app:7") && keys.Contains("ip"));
        }

        // ── Retry / fallback (7, 8, 9, 10) ──────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, idx) => idx == 0 ? Json(429, "").WithRetryAfter(1) : Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, AnyAuth);
            var sw = Stopwatch.StartNew();
            var r = await client.GetAsync<Probe>("/busy/");
            sw.Stop();
            Check("7 429 + Retry-After → wait then retry (2 calls, waited)", r.IsSuccess && stub.Calls == 2 && sw.ElapsedMilliseconds >= 120);
        }
        {
            var stub = new StubHttpMessageHandler((_, idx) => idx < 2 ? Json(503, "{\"error\":\"down\"}") : Json(200, ProbeBody));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/flaky/");
            Check("8 5xx,5xx,200 → backoff then success (3 calls)", r.IsSuccess && r.Value!.Value == 42 && stub.Calls == 3);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(503, "{\"error\":\"down\"}"));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/down/");
            Check("9 5xx exhausted → SERVER_ERROR, no crash (maxRetries+1 calls)", !r.IsSuccess && r.Error!.Kind == EsiErrorKind.ServerError && stub.Calls == 4);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("simulated timeout"));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/slow/");
            Check("10 transport timeout → TIMEOUT, no crash", !r.IsSuccess && r.Error!.Kind == EsiErrorKind.Timeout && stub.Calls == 4);
        }

        // ── Body handling (12 + parse) ──────────────────────────────────────────────────────────────
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(400, "{\"error\":\"bad param foo\"}"));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/bad/");
            Check("12 400 + error body → BAD_REQUEST carrying the body text, no retry",
                !r.IsSuccess && r.Error!.Kind == EsiErrorKind.BadRequest && r.Error.Message.Contains("bad param foo") && stub.Calls == 1);
        }
        {
            var stub = new StubHttpMessageHandler((_, _) => Json(200, "{not valid json"));
            var (client, _, _) = Wire(stub, AnyAuth);
            var r = await client.GetAsync<Probe>("/garbage/");
            Check("8b 200 + invalid body → PARSE_ERROR, no crash", !r.IsSuccess && r.Error!.Kind == EsiErrorKind.ParseError && stub.Calls == 1);
        }

        // ── File-store persistence + purge (16) ─────────────────────────────────────────────────────
        {
            var dir = Path.Combine(Path.GetTempPath(), "esi-test-store-" + Guid.NewGuid().ToString("N"));
            var store1 = new FileEsiCacheStore(dir);
            await store1.SetAsync("PERSIST", new EsiCacheEntry(ProbeBody, null, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow));
            var persisted = (await new FileEsiCacheStore(dir).GetAsync("PERSIST"))?.Body == ProbeBody;

            var store2 = new FileEsiCacheStore(dir);
            await store2.SetAsync("EXPIRED", new EsiCacheEntry("x", null, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(-10)));
            var purged = await store2.PurgeExpiredAsync();
            var gone = await new FileEsiCacheStore(dir).GetAsync("EXPIRED");
            Directory.Delete(dir, recursive: true);
            Check("16 file-store persists across restart + purge removes expired", persisted && purged >= 1 && gone is null);
        }

        Console.WriteLine(failures == 0
            ? "RESULT: PASS ✓ (all ESI fallbacks verified)"
            : $"RESULT: FAIL ✗ ({failures} scenario(s) failed)");
        return failures == 0 ? 0 : 1;
    }

    private static readonly EsiAuthorization AnyAuth = EsiAuthorization.Authorized("token");

    private static (IEsiClient Client, EsiRateLimitMonitor Monitor, FileEsiCacheStore Store) Wire(
        StubHttpMessageHandler stub, EsiAuthorization auth)
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);
        var store = new FileEsiCacheStore(Path.Combine(Path.GetTempPath(), "esi-test-" + Guid.NewGuid().ToString("N")));
        var policy = new EsiRetryPolicy(
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
            TimeSpan.FromMilliseconds(200));

        var outageDetector = new EsiOutageDetector(new EsiAvailabilityState());
        var retry = new EsiRetryHandler(policy, outageDetector, NullLogger<EsiRetryHandler>.Instance) { InnerHandler = stub };
        var rateLimit = new EsiRateLimitHandler(monitor, NullLogger<EsiRateLimitHandler>.Instance) { InnerHandler = retry };
        var cache = new EsiCacheHandler(store, monitor) { InnerHandler = rateLimit };
        var header = new EsiHeaderHandler(new RuntimeContext(ExecutionHost.Client)) { InnerHandler = cache };

        var http = new HttpClient(header);
        var client = new EsiClient(new SingleClientHttpFactory(http), new FakeEsiTokenProvider(auth),
            outageDetector, NullLogger<EsiClient>.Instance);
        return (client, monitor, store);
    }
}
