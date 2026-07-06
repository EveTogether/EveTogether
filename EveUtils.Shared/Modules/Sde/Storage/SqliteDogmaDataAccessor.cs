using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Data.Sqlite;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// SQLite-backed <see cref="IDogmaDataAccessor"/>. Mirrors <see cref="SqliteSdeAccessor"/>'s connection strategy
/// (short-lived pooled read-only connections keyed on the connection string, so the two accessors share the same
/// pool over the same file) and adds per-instance memoisation: attribute metadata, effect definitions (with their
/// parsed <c>modifierInfo</c>), per-type base attributes/effects and type-&gt;category are cached on first read.
/// <see cref="PrefetchAsync"/> warms those caches with batched <c>IN</c> queries so the calculation loop is IO-free.
/// </summary>
public sealed class SqliteDogmaDataAccessor : IDogmaDataAccessor
{
    private const long MmapSize = 256L * 1024 * 1024;
    private const int ShipCategoryId = 6;     // ships get the synthetic velocityBoost effect + a mass base attribute
    private const int MassAttributeId = 4;
    private const int TacticalDestroyerGroupId = 1305;   // ships that have a tactical mode
    private const int ShipModifierGroupId = 1306;        // the "Ship Modifiers" group the mode items live in

    private readonly string _databasePath;
    private readonly object _gate = new();
    private string? _connectionString;
    private bool _available;

    private ConcurrentDictionary<int, DogmaAttributeMeta?> _attributeMeta = new();
    private ConcurrentDictionary<int, DogmaEffectDef?> _effects = new();
    private ConcurrentDictionary<int, IReadOnlyList<SdeDogmaAttribute>> _baseAttributes = new();
    private ConcurrentDictionary<int, IReadOnlyList<DogmaTypeEffect>> _typeEffects = new();
    private ConcurrentDictionary<int, int?> _categoryId = new();
    private ConcurrentDictionary<int, int?> _groupId = new();
    private ConcurrentDictionary<int, double?> _mass = new();
    private ConcurrentDictionary<int, double?> _capacity = new();
    private ConcurrentDictionary<int, double?> _volume = new();
    private ConcurrentDictionary<int, int?> _tacticalMode = new();
    private IReadOnlyList<int>? _skillTypeIds;

    public SqliteDogmaDataAccessor(string databasePath)
    {
        _databasePath = databasePath;
        LoadState();
    }

    public void Reopen()
    {
        // Drop pooled connections bound to the pre-swap file, then re-read metadata and clear the memo caches.
        SqliteConnection.ClearAllPools();
        _attributeMeta = new ConcurrentDictionary<int, DogmaAttributeMeta?>();
        _effects = new ConcurrentDictionary<int, DogmaEffectDef?>();
        _baseAttributes = new ConcurrentDictionary<int, IReadOnlyList<SdeDogmaAttribute>>();
        _typeEffects = new ConcurrentDictionary<int, IReadOnlyList<DogmaTypeEffect>>();
        _categoryId = new ConcurrentDictionary<int, int?>();
        _groupId = new ConcurrentDictionary<int, int?>();
        _mass = new ConcurrentDictionary<int, double?>();
        _capacity = new ConcurrentDictionary<int, double?>();
        _volume = new ConcurrentDictionary<int, double?>();
        _tacticalMode = new ConcurrentDictionary<int, int?>();
        lock (_gate)
            _skillTypeIds = null;
        LoadState();
    }

    public DogmaAttributeMeta? GetAttributeMeta(int attributeId) =>
        _attributeMeta.GetOrAdd(attributeId, LoadAttributeMeta);

    public IReadOnlyList<SdeDogmaAttribute> GetBaseAttributes(int typeId)
    {
        // A synthetic abyssal beacon (group 1983 is SDE-empty) carries its multiplier/bonus values here, not in the store.
        if (AbyssalBeacons.Get(typeId) is { } beacon)
            return beacon.BaseAttributes;
        var attributes = _baseAttributes.GetOrAdd(typeId, LoadBaseAttributes);
        // Seed the ship's mass as a base attribute (attr 4) from the Type row (a type-field override), so module
        // mass additions apply through the pipeline. Only ships need it today; broaden when agility/align-time joins.
        if (GetCategoryId(typeId) != ShipCategoryId || attributes.Any(attribute => attribute.AttributeId == MassAttributeId))
            return attributes;
        if (GetMass(typeId) is not { } mass)
            return attributes;
        return [.. attributes, new SdeDogmaAttribute(MassAttributeId, mass)];
    }

    public IReadOnlyList<DogmaTypeEffect> GetTypeEffects(int typeId)
    {
        // A synthetic abyssal beacon attaches the real single-purpose system effects it reuses (3992 systemShieldHP, …).
        if (AbyssalBeacons.Get(typeId) is { } beacon)
            return beacon.EffectIds.Select(id => new DogmaTypeEffect(id, IsDefault: false)).ToList();
        var effects = _typeEffects.GetOrAdd(typeId, LoadTypeEffects);
        // Attach by-category synthetic effects (e.g. velocityBoost on every ship) without touching per-type SDE data.
        var linked = DogmaPatches.EffectIdsForCategory(GetCategoryId(typeId) ?? 0);
        if (linked.Count == 0)
            return effects;
        var merged = effects.ToList();
        foreach (var effectId in linked)
            if (merged.All(effect => effect.EffectId != effectId))
                merged.Add(new DogmaTypeEffect(effectId, IsDefault: false));
        return merged;
    }

    public DogmaEffectDef? GetEffect(int effectId) =>
        _effects.GetOrAdd(effectId, LoadEffect);

    public int? GetCategoryId(int typeId) =>
        AbyssalBeacons.Get(typeId) is not null ? AbyssalBeacons.CategoryId : _categoryId.GetOrAdd(typeId, LoadCategoryId);

    public int? GetGroupId(int typeId) =>
        AbyssalBeacons.Get(typeId) is not null ? AbyssalBeacons.GroupId : _groupId.GetOrAdd(typeId, LoadGroupId);

    public double? GetMass(int typeId) =>
        _mass.GetOrAdd(typeId, typeId => LoadTypeField(typeId, "mass"));

    public int? GetDefaultTacticalModeTypeId(int shipTypeId) =>
        _tacticalMode.GetOrAdd(shipTypeId, LoadTacticalModeTypeId);

    public double? GetCapacity(int typeId) =>
        _capacity.GetOrAdd(typeId, typeId => LoadTypeField(typeId, "capacity"));

    public double? GetVolume(int typeId) =>
        _volume.GetOrAdd(typeId, typeId => LoadTypeField(typeId, "volume"));

    private double? LoadTypeField(int typeId, string column)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM Type WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        return command.ExecuteScalar() is double value ? value : null;
    }

    public IReadOnlyList<int> GetSkillTypeIds()
    {
        lock (_gate)
        {
            if (_skillTypeIds is not null)
                return _skillTypeIds;
        }

        var result = new List<int>();
        using (var connection = Open())
        {
            if (connection is not null)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT t.typeId FROM Type t JOIN InvGroup g ON g.groupId = t.groupId WHERE g.categoryId = 16 AND t.published = 1;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetInt32(0));
            }
        }

        lock (_gate)
            return _skillTypeIds ??= result;
    }

    public async Task PrefetchAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default)
    {
        var connectionString = ConnectionString();
        if (connectionString is null || typeIds.Count == 0)
            return;

        // base attributes presence is the per-type "prefetched" marker (all three per-type caches fill together).
        var pending = new HashSet<int>(typeIds);
        pending.RemoveWhere(_baseAttributes.ContainsKey);
        if (pending.Count == 0)
            return;
        var ids = pending.ToList();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only=1; PRAGMA mmap_size=" + MmapSize + ";";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        var baseByType = ids.ToDictionary(id => id, _ => new List<SdeDogmaAttribute>());
        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildInClause(command, ids);
            command.CommandText = $"SELECT typeId, attributeId, value FROM TypeDogmaAttribute WHERE typeId IN ({inClause});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                baseByType[reader.GetInt32(0)].Add(new SdeDogmaAttribute(reader.GetInt32(1), reader.GetDouble(2)));
        }
        foreach (var (typeId, attributes) in baseByType)
            _baseAttributes[typeId] = attributes;

        var effectsByType = ids.ToDictionary(id => id, _ => new List<DogmaTypeEffect>());
        var effectIds = new HashSet<int>();
        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildInClause(command, ids);
            command.CommandText = $"SELECT typeId, effectId, isDefault FROM TypeDogmaEffect WHERE typeId IN ({inClause});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var effectId = reader.GetInt32(1);
                effectsByType[reader.GetInt32(0)].Add(new DogmaTypeEffect(effectId, reader.GetInt64(2) != 0));
                effectIds.Add(effectId);
            }
        }
        foreach (var (typeId, effects) in effectsByType)
            _typeEffects[typeId] = effects;

        await using (var command = connection.CreateCommand())
        {
            var inClause = BuildInClause(command, ids);
            command.CommandText =
                $"SELECT t.typeId, t.groupId, g.categoryId, t.mass FROM Type t JOIN InvGroup g ON g.groupId = t.groupId WHERE t.typeId IN ({inClause});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                _groupId[reader.GetInt32(0)] = reader.GetInt32(1);
                _categoryId[reader.GetInt32(0)] = reader.GetInt32(2);
                _mass[reader.GetInt32(0)] = reader.GetDouble(3);
            }
        }

        // Effect definitions for the collected effects, then the attribute metadata they (and the base rows) touch —
        // so every value the calculation reads is already in memory. The fit's type set keeps these well under the
        // SQLite bound-parameter limit (32766).
        var attributeIds = new HashSet<int>();
        foreach (var attributes in baseByType.Values)
            foreach (var attribute in attributes)
                attributeIds.Add(attribute.AttributeId);

        var pendingEffects = effectIds.Where(id => !_effects.ContainsKey(id)).ToList();
        if (pendingEffects.Count > 0)
        {
            await using var command = connection.CreateCommand();
            var inClause = BuildInClause(command, pendingEffects);
            command.CommandText =
                $"SELECT effectId, effectCategoryId, modifierInfoJson, name FROM DogmaEffect WHERE effectId IN ({inClause});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var effect = ReadEffect(reader);
                _effects[effect.EffectId] = effect;
                foreach (var modifier in effect.Modifiers)
                {
                    if (modifier.ModifiedAttributeId is { } modified)
                        attributeIds.Add(modified);
                    if (modifier.ModifyingAttributeId is { } modifying)
                        attributeIds.Add(modifying);
                }
            }
        }

        var pendingAttributes = attributeIds.Where(id => !_attributeMeta.ContainsKey(id)).ToList();
        if (pendingAttributes.Count > 0)
        {
            await using var command = connection.CreateCommand();
            var inClause = BuildInClause(command, pendingAttributes);
            command.CommandText =
                $"SELECT attributeId, defaultValue, stackable, highIsGood, maxAttributeId FROM DogmaAttribute WHERE attributeId IN ({inClause});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var meta = ReadAttributeMeta(reader);
                _attributeMeta[meta.AttributeId] = meta;
            }
        }
    }

    private DogmaAttributeMeta? LoadAttributeMeta(int attributeId)
    {
        if (DogmaPatches.AttributeMeta(attributeId) is { } synthetic)
            return synthetic;
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT attributeId, defaultValue, stackable, highIsGood, maxAttributeId FROM DogmaAttribute WHERE attributeId = $id;";
        command.Parameters.AddWithValue("$id", attributeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAttributeMeta(reader) : null;
    }

    private IReadOnlyList<SdeDogmaAttribute> LoadBaseAttributes(int typeId)
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

    private IReadOnlyList<DogmaTypeEffect> LoadTypeEffects(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT effectId, isDefault FROM TypeDogmaEffect WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        var result = new List<DogmaTypeEffect>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new DogmaTypeEffect(reader.GetInt32(0), reader.GetInt64(1) != 0));
        return result;
    }

    private DogmaEffectDef? LoadEffect(int effectId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT effectId, effectCategoryId, modifierInfoJson, name FROM DogmaEffect WHERE effectId = $id;";
        command.Parameters.AddWithValue("$id", effectId);
        using var reader = command.ExecuteReader();
        if (reader.Read())
            return ReadEffect(reader);
        // No SDE row: a brand-new custom effect (e.g. the synthetic velocityBoost) lives only in the patch table.
        return DogmaPatches.TryGetEffectPatch(effectId, out var patch)
            ? new DogmaEffectDef(effectId, patch.EffectCategoryId, patch.Modifiers)
            : null;
    }

    private int? LoadCategoryId(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT g.categoryId FROM Type t JOIN InvGroup g ON g.groupId = t.groupId WHERE t.typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        return command.ExecuteScalar() is long value ? (int)value : null;
    }

    private int? LoadGroupId(int typeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT groupId FROM Type WHERE typeId = $id;";
        command.Parameters.AddWithValue("$id", typeId);
        return command.ExecuteScalar() is long value ? (int)value : null;
    }

    public IReadOnlyList<SdeNamedType> GetTacticalModes(int shipTypeId)
    {
        using var connection = Open();
        if (connection is null)
            return [];
        using var command = connection.CreateCommand();
        // All stance modes for the ship: group-1306 "Ship Modifiers" named after the ship, ordered by type id so the
        // Defense mode (the default) comes first. Non-T3D ships match none.
        command.CommandText =
            """
            SELECT mode.typeId, mode.nameEn FROM Type mode
            JOIN Type ship ON ship.typeId = $ship
            WHERE ship.groupId = $shipGroup AND mode.groupId = $modeGroup AND mode.nameEn LIKE ship.nameEn || ' %'
            ORDER BY mode.typeId;
            """;
        command.Parameters.AddWithValue("$ship", shipTypeId);
        command.Parameters.AddWithValue("$shipGroup", TacticalDestroyerGroupId);
        command.Parameters.AddWithValue("$modeGroup", ShipModifierGroupId);
        var result = new List<SdeNamedType>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SdeNamedType(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private int? LoadTacticalModeTypeId(int shipTypeId)
    {
        using var connection = Open();
        if (connection is null)
            return null;
        using var command = connection.CreateCommand();
        // Mode sourcing: a T3D's modes are the "Ship Modifiers" (group 1306) named after the ship; the lowest
        // type id is the Defense mode (the default for an imported fit). Non-T3D ships match none.
        command.CommandText =
            """
            SELECT mode.typeId FROM Type mode
            JOIN Type ship ON ship.typeId = $ship
            WHERE ship.groupId = $shipGroup AND mode.groupId = $modeGroup AND mode.nameEn LIKE ship.nameEn || ' %'
            ORDER BY mode.typeId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ship", shipTypeId);
        command.Parameters.AddWithValue("$shipGroup", TacticalDestroyerGroupId);
        command.Parameters.AddWithValue("$modeGroup", ShipModifierGroupId);
        return command.ExecuteScalar() is long value ? (int)value : null;
    }

    private string? ConnectionString()
    {
        lock (_gate)
            return _available ? _connectionString : null;
    }

    private void LoadState()
    {
        lock (_gate)
        {
            if (!File.Exists(_databasePath))
            {
                _available = false;
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
        }
    }

    private SqliteConnection? Open()
    {
        var connectionString = ConnectionString();
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

    private static string BuildInClause(SqliteCommand command, IReadOnlyList<int> ids)
    {
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "$p" + i;
            command.Parameters.AddWithValue(names[i], ids[i]);
        }
        return string.Join(",", names);
    }

    private static DogmaAttributeMeta ReadAttributeMeta(DbDataReader reader) =>
        new(reader.GetInt32(0), reader.GetDouble(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetInt32(4));

    private static DogmaEffectDef ReadEffect(DbDataReader reader)
    {
        var effectId = reader.GetInt32(0);
        var categoryId = reader.GetInt32(1);
        var json = reader.IsDBNull(2) ? null : reader.GetString(2);
        var name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var modifiers = ParseModifiers(json);
        // Overlay a synthetic patch only for effects CCP left without modifiers (the empty-modifierInfo omissions);
        // the SDE category is kept. A real effect with modifiers is never overridden.
        if (modifiers.Count == 0 && DogmaPatches.TryGetEffectPatch(effectId, out var patch))
            modifiers = patch.Modifiers;
        return new DogmaEffectDef(effectId, categoryId, modifiers, name);
    }

    private static IReadOnlyList<ModifierInfo> ParseModifiers(string? modifierInfoJson)
    {
        if (string.IsNullOrEmpty(modifierInfoJson))
            return [];
        using var document = JsonDocument.Parse(modifierInfoJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<ModifierInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
            result.Add(new ModifierInfo(
                ParseFunc(GetString(element, "func")),
                ParseDomain(GetString(element, "domain")),
                GetInt(element, "operation") ?? ModifierInfo.NoOperation,
                GetInt(element, "modifiedAttributeID"),
                GetInt(element, "modifyingAttributeID"),
                GetInt(element, "groupID"),
                GetInt(element, "skillTypeID")));
        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static ModifierFunc ParseFunc(string? func) => func switch
    {
        "ItemModifier" => ModifierFunc.ItemModifier,
        "LocationModifier" => ModifierFunc.LocationModifier,
        "LocationGroupModifier" => ModifierFunc.LocationGroupModifier,
        "LocationRequiredSkillModifier" => ModifierFunc.LocationRequiredSkillModifier,
        "OwnerRequiredSkillModifier" => ModifierFunc.OwnerRequiredSkillModifier,
        "EffectStopper" => ModifierFunc.EffectStopper,
        _ => ModifierFunc.Unknown
    };

    private static ModifierDomain ParseDomain(string? domain) => domain switch
    {
        "itemID" => ModifierDomain.ItemId,
        "shipID" => ModifierDomain.ShipId,
        "charID" => ModifierDomain.CharId,
        "otherID" => ModifierDomain.OtherId,
        "structureID" => ModifierDomain.StructureId,
        "target" => ModifierDomain.Target,
        "targetID" => ModifierDomain.TargetId,
        _ => ModifierDomain.Unknown
    };
}
