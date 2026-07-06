using EveUtils.Shared.Modules.Ships;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Data;

/// <summary>
/// Base context with the <b>shared</b> modules. Deliberately <c>abstract</c> + <c>protected</c>
/// constructor: not to be injected directly — only to be extended by
/// <see cref="ClientDbContext"/> and <see cref="ServerDbContext"/>. The model is built per
/// module via <c>XxxModule.ConfigureModel(...)</c>; entities never leave the module.
/// Repositories access the tables via <c>Set&lt;T&gt;()</c> (no public DbSets).
/// </summary>
public abstract class SharedDbContext : DbContext
{
    protected SharedDbContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ShipsModule.ConfigureModel(modelBuilder);
    }
}
