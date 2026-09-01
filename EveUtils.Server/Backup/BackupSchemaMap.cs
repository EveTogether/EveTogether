using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EveUtils.Server.Backup;

/// <summary>
/// Reads the tables to back up straight out of the EF model, in an order a restore can insert them in.
///
/// Columns come from the model rather than from the entity classes, which is what makes owned entities a
/// non-event: <c>FleetMember.AssignedFit</c> is table-split onto the member row, so its <c>AssignedFit_*</c>
/// columns are simply part of that table here and need no special case. It also picks up shadow properties and
/// anything a value converter reshaped, both of which an entity-by-entity export loses.
/// </summary>
internal static class BackupSchemaMap
{
    /// <summary>Every mapped table, ordered so a table is always inserted after the tables it points at.</summary>
    public static IReadOnlyList<BackupTableMap> Build(IModel model)
    {
        var tables = new Dictionary<StoreObjectIdentifier, TableBuilder>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (storeObject is not { } table)
                continue;

            if (!tables.TryGetValue(table, out var builder))
                tables[table] = builder = new TableBuilder(table);

            builder.Add(entityType, table);
        }

        foreach (var builder in tables.Values)
            builder.ResolveDependencies(tables.Keys);

        return _Order([.. tables.Values]);
    }

    /// <summary>
    /// Kahn's algorithm over the foreign-key graph, ties broken on table name so two runs of the same model
    /// produce the same archive. A cycle would mean no insert order exists at all; the model has none today and a
    /// test holds that line, so this reports it by name rather than silently emitting an order that fails halfway
    /// through someone's restore.
    /// </summary>
    private static List<BackupTableMap> _Order(List<TableBuilder> builders)
    {
        var remaining = builders.ToDictionary(b => b.Table, b => new HashSet<StoreObjectIdentifier>(b.DependsOn));
        var ordered = new List<BackupTableMap>(builders.Count);
        var byTable = builders.ToDictionary(b => b.Table);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(entry => entry.Value.Count == 0)
                .Select(entry => entry.Key)
                .OrderBy(table => table.Name, StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0)
            {
                var cycle = string.Join(", ", remaining.Keys.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
                throw new InvalidOperationException(
                    $"The database model has a cycle of foreign keys ({cycle}), so no insert order exists. " +
                    "A backup cannot be restored table by table until that cycle is broken.");
            }

            foreach (var table in ready)
            {
                ordered.Add(byTable[table].Build());
                remaining.Remove(table);
            }

            foreach (var dependencies in remaining.Values)
                dependencies.ExceptWith(ready);
        }

        return ordered;
    }

    private sealed class TableBuilder(StoreObjectIdentifier table)
    {
        private readonly Dictionary<string, BackupColumnType> _columns = [];
        private readonly SortedSet<string> _keyColumns = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _storeGeneratedKeyColumns = new(StringComparer.Ordinal);
        private readonly List<IEntityType> _entityTypes = [];

        public StoreObjectIdentifier Table { get; } = table;

        public HashSet<StoreObjectIdentifier> DependsOn { get; } = [];

        public void Add(IEntityType entityType, StoreObjectIdentifier storeObject)
        {
            _entityTypes.Add(entityType);

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column is null || _columns.ContainsKey(column))
                    continue;

                var clrType = property.GetRelationalTypeMapping().ClrType;
                _columns[column] = BackupValueCodec.FromClrType(clrType)
                    ?? throw new InvalidOperationException(
                        $"Column '{storeObject.Name}.{column}' stores a {clrType.Name}, which the backup format " +
                        "cannot represent. Add it to BackupColumnType before shipping this column.");

                if (!property.IsPrimaryKey())
                    continue;

                _keyColumns.Add(column);
                if (property.ValueGenerated.HasFlag(ValueGenerated.OnAdd))
                    _storeGeneratedKeyColumns.Add(column);
            }
        }

        /// <summary>Foreign keys to other tables. A key that stays inside this table — an owned type pointing back
        /// at the row it is split onto, or a self-reference — is not an ordering constraint between tables.</summary>
        public void ResolveDependencies(IEnumerable<StoreObjectIdentifier> knownTables)
        {
            var known = knownTables.ToHashSet();

            foreach (var foreignKey in _entityTypes.SelectMany(e => e.GetForeignKeys()))
            {
                var principal = StoreObjectIdentifier.Create(foreignKey.PrincipalEntityType, StoreObjectType.Table);
                if (principal is { } target && target != Table && known.Contains(target))
                    DependsOn.Add(target);
            }
        }

        public BackupTableMap Build() => new(
            Table.Name,
            Table.Schema,
            [.. _columns.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new BackupTableColumn(c.Key, c.Value))],
            [.. _keyColumns],
            [.. _storeGeneratedKeyColumns]);
    }
}
