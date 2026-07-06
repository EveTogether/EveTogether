using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Data.Sqlite;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// Read-only <see cref="ISdeAccessor"/> over a memory-mapped SQLite store. Opens short-lived pooled read-only
/// connections per query (pooling keys on the connection string, so reuse is cheap) — read-only SQLite is safe
/// for concurrent reads, so no locking on the read path. <see cref="Reopen"/> clears the pool so a freshly
/// swapped-in file is picked up. The build version is cached and refreshed on open/reopen.
/// </summary>
public sealed class SqliteSdeAccessor : ISdeAccessor
{
    private const long MmapSize = 256L * 1024 * 1024;

    private readonly string _databasePath;
    private readonly object _gate = new();
    private string? _connectionString;
    private SdeVersion? _version;
    private bool _available;

    public SqliteSdeAccessor(string databasePath)
    {
        _databasePath = databasePath;
        LoadState();
    }

    public bool IsAvailable
    {
        get { lock (_gate) return _available; }
    }

    public SdeVersion? Version
    {
        get { lock (_gate) return _version; }
    }

    public void Close()
    {
        // Stop serving queries and drop pooled connections so the store file handle is released — the importer must
        // be able to overwrite it during the atomic swap (a pooled/mmap handle blocks File.Move on Windows).
        lock (_gate)
        {
            _available = false;
            _connectionString = null;
        }
        SqliteConnection.ClearAllPools();
    }

    public void Reopen()
    {
        // Drop any pooled connections still bound to the pre-swap file, then re-read the build metadata.
        SqliteConnection.ClearAllPools();
        LoadState();
    }

    private void LoadState()
    {
        lock (_gate)
        {
            if (!File.Exists(_databasePath))
            {
                _available = false;
                _version = null;
                _connectionString = null;
                return;
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true
            }.ToString();
            _available = true;
            // A store built by an older app version may lack columns this build queries (e.g. maxAttributeId).
            // Treat a schema mismatch as no usable store so the importer rebuilds it on the next check.
            if (ReadSchemaVersion() != SdeSchema.SchemaVersion)
            {
                _available = false;
                _version = null;
                _connectionString = null;
                return;
            }
            _version = ReadVersion();
        }
    }

    private SqliteConnection? Open()
    {
        string? connectionString;
        lock (_gate)
            connectionString = _available ? _connectionString : null;
        if (connectionString is null)
            return null;

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=1; PRAGMA mmap_size=" + MmapSize + ";";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    // The schema version stored at build time; absent (pre-v2 store) reads as 1 so it is rebuilt.
    private int ReadSchemaVersion()
    {
        try
        {
            using var connection = Open();
            if (connection is null)
                return 0;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM Meta WHERE key = $k;";
            command.Parameters.AddWithValue("$k", SdeSchema.MetaSchemaVersion);
            return command.ExecuteScalar() is string value && int.TryParse(value, out var schema) ? schema : 1;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private SdeVersion? ReadVersion()
    {
        try
        {
            using var connection = Open();
            if (connection is null)
                return null;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM Meta WHERE key IN ($b, $r);";
            command.Parameters.AddWithValue("$b", SdeSchema.MetaBuildNumber);
            command.Parameters.AddWithValue("$r", SdeSchema.MetaReleaseDate);
            long build = 0;
            DateTimeOffset release = default;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (key == SdeSchema.MetaBuildNumber)
                    long.TryParse(value, out build);
                else if (key == SdeSchema.MetaReleaseDate)
                    DateTimeOffset.TryParse(value, out release);
            }
            return build > 0 ? new SdeVersion(build, release) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public bool TryGetTypeName(int typeId, out string name)
    {
        name = string.Empty;
        using var connection = Open();
        if (connection is null)
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT nameEn FROM Type WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        if (command.ExecuteScalar() is string value)
        {
            name = value;
            return true;
        }
        return false;
    }

    public bool TryGetTypeId(string name, out int typeId)
    {
        typeId = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        using var connection = Open();
        if (connection is null)
            return false;
        using var command = connection.CreateCommand();
        // Match the canonical English name first (pri 0), then a locale alias (pri 1); within each, prefer a
        // published type. Deterministic so a cross-locale name collision resolves to the English/published type
        // rather than an arbitrary row.
        command.CommandText =
            """
            SELECT typeId FROM (
                SELECT typeId, published, 0 AS pri FROM Type WHERE nameKey = $key
                UNION ALL
                SELECT a.typeId, t.published, 1 AS pri FROM TypeNameAlias a JOIN Type t ON t.typeId = a.typeId WHERE a.nameKey = $key
            )
            ORDER BY pri, published DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", NameKey(name));
        if (command.ExecuteScalar() is long value)
        {
            typeId = (int)value;
            return true;
        }
        return false;
    }

    public SdeType? GetType(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT typeId, groupId, nameEn, published, mass, volume, capacity, marketGroupId FROM Type WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SdeType(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7));
    }

    public IReadOnlyList<SdeDogmaAttribute> GetDogmaAttributes(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attributeId, value FROM TypeDogmaAttribute WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        var result = new List<SdeDogmaAttribute>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SdeDogmaAttribute(reader.GetInt32(0), reader.GetDouble(1)));
        return result;
    }

    public SdeFitRequirement? GetFitRequirement(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT slotType, numberOfSlots, isLauncher, isTurret FROM TypeFitRequirement WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SdeFitRequirement(
            (SdeSlotType)reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0);
    }

    public SdeSlotType GetSlotType(int typeId) => GetFitRequirement(typeId)?.SlotType ?? SdeSlotType.None;

    public IReadOnlyList<SdeChargeType> GetChargeTypesInGroup(int groupId)
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        // Charge size (attr 128) lives in TypeDogmaAttribute; left-join so unsized charges (missiles/scripts) still list.
        command.CommandText =
            "SELECT t.typeId, t.nameEn, a.value " +
            "FROM Type t LEFT JOIN TypeDogmaAttribute a ON a.typeId = t.typeId AND a.attributeId = 128 " +
            "WHERE t.groupId = $g AND t.published = 1 ORDER BY t.nameEn;";
        command.Parameters.AddWithValue("$g", groupId);
        var result = new List<SdeChargeType>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SdeChargeType(reader.GetInt32(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2)));
        return result;
    }

    public IReadOnlyList<SdeNamedType> GetBoosterTypes()
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        // Boosters carry the "boosterness" attribute (1087, the 1-3 booster slot); implants carry "implantness" (331).
        // Joining on 1087 selects boosters without depending on a category/group list.
        command.CommandText =
            "SELECT t.typeId, t.nameEn FROM Type t " +
            "JOIN TypeDogmaAttribute a ON a.typeId = t.typeId AND a.attributeId = 1087 " +
            "WHERE t.published = 1 ORDER BY t.nameEn;";
        var result = new List<SdeNamedType>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SdeNamedType(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    // The inventory category holding every fighter type (ship + Standup structure variants).
    private const int FighterCategoryId = 87;

    public IReadOnlyList<SdeNamedType> GetFighterTypes()
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.typeId, t.nameEn FROM Type t JOIN InvGroup g ON g.groupId = t.groupId " +
            "WHERE g.categoryId = $cat AND t.published = 1 ORDER BY t.nameEn;";
        command.Parameters.AddWithValue("$cat", FighterCategoryId);
        var result = new List<SdeNamedType>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SdeNamedType(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    // The inventory group holding every environment "Effect Beacon" (wormhole/metaliminal/incursion/… system effects).
    private const int EffectBeaconGroupId = 920;

    public IReadOnlyList<SdeEnvironmentBeacon> GetEnvironmentBeacons()
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        // Read every group-920 beacon name; the classifier keeps only the curated, data-driven phenomena. published is
        // not filtered — the relevant beacons are published, and the classifier rejects the rest by name anyway.
        command.CommandText = "SELECT typeId, nameEn FROM Type WHERE groupId = $g;";
        command.Parameters.AddWithValue("$g", EffectBeaconGroupId);
        var result = new List<SdeEnvironmentBeacon>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (EnvironmentBeaconClassifier.Classify(reader.GetInt32(0), reader.GetString(1)) is { } beacon)
                    result.Add(beacon);
        // Append the synthetic abyssal beacons (group 1983 is SDE-empty, so they are reconstructed in the patch layer).
        result.AddRange(AbyssalBeacons.EnvironmentBeacons());
        result.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return result;
    }

    public SdeGroup? GetGroup(int groupId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT groupId, categoryId, nameEn, published FROM InvGroup WHERE groupId = $id;";
        command.Parameters.AddWithValue("$id", groupId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SdeGroup(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3) != 0);
    }

    public IReadOnlyList<SdeGroup> GetGroupsByCategory(int categoryId)
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT groupId, categoryId, nameEn, published FROM InvGroup WHERE categoryId = $cat AND published = 1 ORDER BY nameEn;";
        command.Parameters.AddWithValue("$cat", categoryId);
        using var reader = command.ExecuteReader();
        var groups = new List<SdeGroup>();
        while (reader.Read())
            groups.Add(new SdeGroup(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3) != 0));
        return groups;
    }

    public SdeCategory? GetCategory(int categoryId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT categoryId, nameEn, published FROM Category WHERE categoryId = $id;";
        command.Parameters.AddWithValue("$id", categoryId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new SdeCategory(reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2) != 0);
    }

    public IReadOnlyList<NpcEnemy> SearchNpcEnemies(string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return [];
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        // NPC types live in category 11 ("Entity"). published=0 for all NPCs — do NOT filter by published.
        // Restrict to types that carry at least one damage attribute (114=EM, 116=Exp, 117=Kin, 118=Th) so
        // zero-damage administrative types (stations, structures etc.) are excluded.
        command.CommandText =
            """
            SELECT t.typeId, t.nameEn, g.nameEn
            FROM Type t
            JOIN InvGroup g ON g.groupId = t.groupId
            WHERE g.categoryId = 11
              AND t.nameEn LIKE $pattern
              AND EXISTS (
                  SELECT 1 FROM TypeDogmaAttribute a
                  WHERE a.typeId = t.typeId AND a.attributeId IN (114, 116, 117, 118)
              )
            ORDER BY t.nameEn
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("$pattern", "%" + q.Replace("%", "\\%").Replace("_", "\\_") + "%");
        var result = new List<NpcEnemy>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new NpcEnemy(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public DamageProfile? GetNpcDamageProfile(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        // Verify the type belongs to category 11 and read the four damage attributes (absent = 0).
        command.CommandText =
            """
            SELECT a.attributeId, a.value
            FROM Type t
            JOIN InvGroup g ON g.groupId = t.groupId
            JOIN TypeDogmaAttribute a ON a.typeId = t.typeId AND a.attributeId IN (114, 116, 117, 118)
            WHERE t.typeId = $id AND g.categoryId = 11;
            """;
        command.Parameters.AddWithValue("$id", typeId);
        double em = 0, th = 0, kin = 0, exp = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var attrId = reader.GetInt32(0);
                var value  = reader.GetDouble(1);
                switch (attrId)
                {
                    case 114: em  = value; break;  // EM damage
                    case 118: th  = value; break;  // Thermal damage
                    case 117: kin = value; break;  // Kinetic damage
                    case 116: exp = value; break;  // Explosive damage
                }
            }
        }
        var profile = new DamageProfile(em, th, kin, exp);
        // Normalise; returns Uniform (and thus non-null) when sum is zero.
        var normalised = profile.Normalized();
        // Reject types with no positive damage — Normalized() falls back to Uniform when sum <= 0, but we want null
        // in that case so callers know the type has no meaningful damage profile.
        return (em + th + kin + exp) <= 0 ? null : normalised;
    }

    internal static string NameKey(string name) => name.Trim().ToLowerInvariant();
}
