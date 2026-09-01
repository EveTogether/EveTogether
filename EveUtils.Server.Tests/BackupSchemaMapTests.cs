using EveUtils.Server.Backup;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The table plan the export writes and the restore inserts by. Two things have to hold for a restore to work at
/// all: the order respects the foreign keys, and no column is silently left out.
/// </summary>
public class BackupSchemaMapTests : IDisposable
{
    private readonly MigratedSqliteServerDatabase _database = new();
    private readonly IReadOnlyList<BackupTableMap> _tables;

    public BackupSchemaMapTests()
    {
        using var db = _database.CreateDbContext();
        _tables = BackupSchemaMap.Build(db.Model);
    }

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// Guards the assumption the whole format rests on: with a cycle there is no insert order, and
    /// <see cref="BackupSchemaMap.Build"/> throws rather than emitting one that fails halfway through a restore.
    /// Adding a circular foreign key to the model turns this red before anyone ships it.
    /// </summary>
    [Fact]
    public void Build_ServerModel_HasAnInsertOrderAtAll()
    {
        Assert.NotEmpty(_tables);
    }

    [Fact]
    public void Build_EveryTable_ComesAfterTheTablesItPointsAt()
    {
        using var db = _database.CreateDbContext();
        var position = _tables.Select((table, index) => (table.Name, index))
            .ToDictionary(entry => entry.Name, entry => entry.index, StringComparer.Ordinal);

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var dependent = entityType.GetTableName();
            if (dependent is null)
                continue;

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var principal = foreignKey.PrincipalEntityType.GetTableName();
                if (principal is null || principal == dependent || !position.ContainsKey(principal))
                    continue;

                Assert.True(position[principal] < position[dependent],
                    $"'{principal}' must be inserted before '{dependent}', which has a foreign key to it.");
            }
        }
    }

    /// <summary>
    /// FleetMember.AssignedFit is an owned type table-split onto the member row. Reading columns from the model
    /// rather than from the entity is what makes those columns come along without a special case — the thing the
    /// ticket warns a provider-neutral export gets wrong.
    /// </summary>
    [Fact]
    public void Build_OwnedEntityColumns_AreCarriedOnTheOwnersTable()
    {
        var member = _tables.Single(t => t.Name == "FleetMember");
        var columns = member.Columns.Select(c => c.Name).ToList();

        Assert.Contains("AssignedFit_ContentHash", columns);
        Assert.Contains("AssignedFit_RawJson", columns);
        Assert.Contains("AssignedFit_ShipTypeId", columns);
    }

    [Fact]
    public void Build_KeyColumns_AreMarkedAndRecognisedAsStoreGenerated()
    {
        var synced = _tables.Single(t => t.Name == "SyncedCharacter");

        Assert.Equal(["Id"], synced.KeyColumns);
        Assert.Equal(["Id"], synced.StoreGeneratedKeyColumns);
    }

    /// <summary>The audit table this ticket adds has to be in the plan, or a backup would not carry the record of
    /// who took the previous ones.</summary>
    [Fact]
    public void Build_Includes_TheBackupDownloadAudit()
    {
        Assert.Contains(_tables, t => t.Name == "BackupDownload");
    }
}
