using System.Collections.Concurrent;
using System.Linq;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Transport.Implementations;

/// <summary>
/// SQLite-backed TOFU pin store — replaces the former JSON-file <c>FileServerTrustStore</c>.
/// Persists the pinned fingerprint in the <see cref="CoupledServer.CertFingerprint"/> column (shared row
/// with <see cref="EfServerRegistry"/>; each store writes only its own fields).
/// <para><see cref="IServerTrustStore"/> is synchronous while EF is async, so this keeps an in-memory cache
/// (loaded once, lazily, to avoid touching the DB before the startup migration) with a synchronous
/// write-through on <see cref="Pin"/>. The cache stays coherent because no other store writes the
/// fingerprint column.</para>
/// </summary>
internal sealed class EfServerTrustStore(IDbContextFactory<SharedDbContext> contextFactory) : IServerTrustStore, ISingletonService
{
    private readonly ConcurrentDictionary<string, string> _pins = new();
    private readonly Lock _gate = new();
    private bool _loaded;

    public string? GetFingerprint(string serverAddress)
    {
        EnsureLoaded();
        return _pins.GetValueOrDefault(serverAddress);
    }

    public void Pin(string serverAddress, string fingerprint)
    {
        EnsureLoaded();
        _pins[serverAddress] = fingerprint;

        using var db = contextFactory.CreateDbContext();
        var row = db.Set<CoupledServer>().FirstOrDefault(s => s.Address == serverAddress);
        if (row is null)
            db.Set<CoupledServer>().Add(new CoupledServer { Address = serverAddress, CertFingerprint = fingerprint });
        else
            row.CertFingerprint = fingerprint;
        db.SaveChanges();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            using var db = contextFactory.CreateDbContext();
            var rows = db.Set<CoupledServer>().AsNoTracking()
                .Where(s => s.CertFingerprint != null)
                .Select(s => new { s.Address, s.CertFingerprint })
                .ToList();
            foreach (var r in rows)
                _pins[r.Address] = r.CertFingerprint!;
            _loaded = true;
        }
    }
}
