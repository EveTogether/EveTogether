using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Transport.Implementations;

/// <summary>
/// SQLite-backed server registry — replaces the former JSON-file <c>FileServerRegistry</c>. Stores
/// the user label and the server's own name per address in the <see cref="CoupledServer"/> table, so the
/// UI can show a friendly name instead of the raw URL. Shares the row with
/// <see cref="EfServerTrustStore"/> (which owns <see cref="CoupledServer.CertFingerprint"/>); each store
/// only writes its own fields, so neither clobbers the other.
/// </summary>
internal sealed class EfServerRegistry(IDbContextFactory<SharedDbContext> contextFactory) : IServerRegistry, ISingletonService
{
    public async Task SetAsync(string serverAddress, string? label, string? serverName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<CoupledServer>().FirstOrDefaultAsync(s => s.Address == serverAddress, cancellationToken);
        if (row is null)
        {
            db.Set<CoupledServer>().Add(new CoupledServer { Address = serverAddress, Label = label, ServerName = serverName });
        }
        else
        {
            // Merge: a null argument keeps the previously stored value (label from a couple, name from pairing).
            row.Label = label ?? row.Label;
            row.ServerName = serverName ?? row.ServerName;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ServerInfo?> GetAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Set<CoupledServer>().AsNoTracking().FirstOrDefaultAsync(s => s.Address == serverAddress, cancellationToken);
        return row is null ? null : new ServerInfo(row.Label, row.ServerName);
    }

    public async Task<IReadOnlyDictionary<string, ServerInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<CoupledServer>().AsNoTracking().ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Address, r => new ServerInfo(r.Label, r.ServerName));
    }

    public async Task<string> DisplayNameAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        var info = await GetAsync(serverAddress, cancellationToken);
        return info?.DisplayName(serverAddress) ?? serverAddress;
    }
}
