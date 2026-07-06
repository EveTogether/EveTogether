using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Permissions.Entities;
using EveUtils.Shared.Modules.Permissions.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Permissions.Repositories.Implementations;

/// <summary>
/// SQLite/multi-provider-backed permission-toggle store — replaces the JSON-file store. Persists
/// each toggle in the server-DB <see cref="PermissionToggle"/> table. <see cref="IPermissionToggleStore"/>
/// is synchronous while EF is async, so this keeps an in-memory cache (loaded once, lazily, to avoid
/// touching the DB before the startup migration) with a synchronous write-through on
/// <see cref="SetEnabled"/>. Default is enabled when a code has never been set (default-allow).
/// </summary>
internal sealed class EfPermissionToggleStore(IDbContextFactory<SharedDbContext> contextFactory) : IPermissionToggleStore, ISingletonService
{
    private readonly ConcurrentDictionary<string, bool> _toggles = new();
    private readonly Lock _gate = new();
    private bool _loaded;

    public bool IsEnabled(string code)
    {
        EnsureLoaded();
        return _toggles.GetValueOrDefault(code, true); // default-allow
    }

    public void SetEnabled(string code, bool value)
    {
        EnsureLoaded();
        _toggles[code] = value;

        using var db = contextFactory.CreateDbContext();
        var row = db.Set<PermissionToggle>().FirstOrDefault(t => t.Code == code);
        if (row is null)
            db.Set<PermissionToggle>().Add(new PermissionToggle { Code = code, Enabled = value });
        else
            row.Enabled = value;
        db.SaveChanges();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            using var db = contextFactory.CreateDbContext();
            foreach (var row in db.Set<PermissionToggle>().AsNoTracking().ToList())
                _toggles[row.Code] = row.Enabled;
            _loaded = true;
        }
    }
}
