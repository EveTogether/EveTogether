using System.Collections.Concurrent;
using EveUtils.Grpc;
using Grpc.Core;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Headless proof for the routing seam, runnable via <c>--routing-test</c>. It exercises
/// <see cref="ConnectedClients"/> directly with fake stream writers (no gRPC/auth/DB needed) and asserts:
/// a targeted send reaches ONLY the target character's connections (including a character's multiple
/// connections), a broadcast reaches everyone except the originator, and removing one connection updates
/// the character index. Exit code 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class ConnectedClientsRoutingCheck
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== EVE-Utils routing-seam check (ConnectedClients) ==");

        var clients = new ConnectedClients();

        // charA (id 100) has TWO live connections; charB (id 200) one; a sender (id 300) one.
        var a1 = new RecordingWriter();
        var a2 = new RecordingWriter();
        var b1 = new RecordingWriter();
        var snd = new RecordingWriter();
        clients.Add(new ConnectedClient("a-1", 100, "CharA", a1));
        clients.Add(new ConnectedClient("a-2", 100, "CharA", a2));
        clients.Add(new ConnectedClient("b-1", 200, "CharB", b1));
        clients.Add(new ConnectedClient("snd", 300, "Sender", snd));

        var ct = CancellationToken.None;
        var ok = true;

        // 1. Targeted send → only CharA's two connections.
        await clients.SendToCharacterAsync(100, Env("targeted"), ct);
        ok &= Check("targeted → CharA conn #1 received", a1.Count == 1);
        ok &= Check("targeted → CharA conn #2 received (multi-connection fanout)", a2.Count == 1);
        ok &= Check("targeted → CharB NOT received", b1.Count == 0);
        ok &= Check("targeted → sender NOT received", snd.Count == 0);

        // 2. Broadcast → everyone except the originator (snd).
        await clients.BroadcastExceptAsync("snd", Env("broadcast"), ct);
        ok &= Check("broadcast → CharA conn #1 received", a1.Count == 2);
        ok &= Check("broadcast → CharA conn #2 received", a2.Count == 2);
        ok &= Check("broadcast → CharB received", b1.Count == 1);
        ok &= Check("broadcast → originator excluded", snd.Count == 0);

        // 3. Multi-character send → both CharA (x2) and CharB, deduplicated.
        await clients.SendToCharactersAsync([100, 200, 100], Env("multi"), ct);
        ok &= Check("multi → CharA conn #1 received once (deduped)", a1.Count == 3);
        ok &= Check("multi → CharA conn #2 received once (deduped)", a2.Count == 3);
        ok &= Check("multi → CharB received", b1.Count == 2);
        ok &= Check("multi → sender NOT received", snd.Count == 0);

        // 4. Removing one of CharA's connections updates the index; the remaining one still gets it.
        clients.Remove("a-1");
        await clients.SendToCharacterAsync(100, Env("after-remove"), ct);
        ok &= Check("after remove → dropped connection gets nothing more", a1.Count == 3);
        ok &= Check("after remove → surviving connection still targeted", a2.Count == 4);

        // 5. Unknown / zero target is a safe no-op (no throw).
        await clients.SendToCharacterAsync(999999, Env("nobody"), ct);
        await clients.SendToCharacterAsync(0, Env("zero"), ct);
        ok &= Check("unknown/zero target → safe no-op", a2.Count == 4 && b1.Count == 2);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static EventEnvelope Env(string tag) => new()
    {
        EventType = "routing.test",
        EventId = tag,
        PayloadJson = "{}"
    };

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    /// <summary>A fake <see cref="IServerStreamWriter{T}"/> that just records what was written.</summary>
    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        private readonly ConcurrentBag<ServerEnvelope> _received = [];

        public int Count => _received.Count;
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ServerEnvelope message)
        {
            _received.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            _received.Add(message);
            return Task.CompletedTask;
        }
    }
}
