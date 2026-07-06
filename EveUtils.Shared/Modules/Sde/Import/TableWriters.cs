using System.Text.Json;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Prepared bulk-insert commands for the SDE tables, reused row-by-row inside the build transaction. Owns the
/// per-dataset JSON shape: types/groups/categories carry a localized <c>name.en</c>, dogma attributes/effects a
/// plain <c>name</c> string, and typeDogma drives both the per-type dogma rows and the pre-computed slot table.
/// </summary>
internal sealed class TableWriters
{
    private readonly SqliteCommand _category;
    private readonly SqliteCommand _group;
    private readonly SqliteCommand _dogmaAttribute;
    private readonly SqliteCommand _dogmaEffect;
    private readonly SqliteCommand _type;
    private readonly SqliteCommand _typeDogmaAttribute;
    private readonly SqliteCommand _typeDogmaEffect;
    private readonly SqliteCommand _fitRequirement;
    private readonly SqliteCommand _typeAlias;

    public TableWriters(SqliteConnection connection, SqliteTransaction transaction)
    {
        _category = Prepare(connection, transaction,
            "INSERT INTO Category (categoryId, nameEn, published) VALUES ($categoryId, $nameEn, $published);",
            "$categoryId", "$nameEn", "$published");
        _group = Prepare(connection, transaction,
            "INSERT INTO InvGroup (groupId, categoryId, nameEn, published) VALUES ($groupId, $categoryId, $nameEn, $published);",
            "$groupId", "$categoryId", "$nameEn", "$published");
        _dogmaAttribute = Prepare(connection, transaction,
            "INSERT INTO DogmaAttribute (attributeId, name, displayNameEn, defaultValue, stackable, highIsGood, unitId, published, maxAttributeId) " +
            "VALUES ($attributeId, $name, $displayNameEn, $defaultValue, $stackable, $highIsGood, $unitId, $published, $maxAttributeId);",
            "$attributeId", "$name", "$displayNameEn", "$defaultValue", "$stackable", "$highIsGood", "$unitId", "$published", "$maxAttributeId");
        _dogmaEffect = Prepare(connection, transaction,
            "INSERT INTO DogmaEffect (effectId, name, effectCategoryId, published, modifierInfoJson) " +
            "VALUES ($effectId, $name, $effectCategoryId, $published, $modifierInfoJson);",
            "$effectId", "$name", "$effectCategoryId", "$published", "$modifierInfoJson");
        _type = Prepare(connection, transaction,
            "INSERT INTO Type (typeId, groupId, nameEn, nameKey, published, mass, volume, capacity, marketGroupId) " +
            "VALUES ($typeId, $groupId, $nameEn, $nameKey, $published, $mass, $volume, $capacity, $marketGroupId);",
            "$typeId", "$groupId", "$nameEn", "$nameKey", "$published", "$mass", "$volume", "$capacity", "$marketGroupId");
        _typeDogmaAttribute = Prepare(connection, transaction,
            "INSERT INTO TypeDogmaAttribute (typeId, attributeId, value) VALUES ($typeId, $attributeId, $value);",
            "$typeId", "$attributeId", "$value");
        _typeDogmaEffect = Prepare(connection, transaction,
            "INSERT INTO TypeDogmaEffect (typeId, effectId, isDefault) VALUES ($typeId, $effectId, $isDefault);",
            "$typeId", "$effectId", "$isDefault");
        _fitRequirement = Prepare(connection, transaction,
            "INSERT INTO TypeFitRequirement (typeId, slotType, numberOfSlots, isLauncher, isTurret) " +
            "VALUES ($typeId, $slotType, $numberOfSlots, $isLauncher, $isTurret);",
            "$typeId", "$slotType", "$numberOfSlots", "$isLauncher", "$isTurret");
        _typeAlias = Prepare(connection, transaction,
            "INSERT INTO TypeNameAlias (typeId, nameKey, locale) VALUES ($typeId, $nameKey, $locale);",
            "$typeId", "$nameKey", "$locale");
    }

    public void Insert(string dataset, JsonElement element)
    {
        switch (dataset)
        {
            case "categories.jsonl": InsertCategory(element); break;
            case "groups.jsonl": InsertGroup(element); break;
            case "dogmaAttributes.jsonl": InsertDogmaAttribute(element); break;
            case "dogmaEffects.jsonl": InsertDogmaEffect(element); break;
            case "types.jsonl": InsertType(element); break;
            case "typeDogma.jsonl": InsertTypeDogma(element); break;
        }
    }

    private void InsertCategory(JsonElement e)
    {
        _category.Parameters["$categoryId"].Value = Key(e);
        _category.Parameters["$nameEn"].Value = EnName(e, "name");
        _category.Parameters["$published"].Value = Bool(e, "published");
        _category.ExecuteNonQuery();
    }

    private void InsertGroup(JsonElement e)
    {
        _group.Parameters["$groupId"].Value = Key(e);
        _group.Parameters["$categoryId"].Value = Int(e, "categoryID");
        _group.Parameters["$nameEn"].Value = EnName(e, "name");
        _group.Parameters["$published"].Value = Bool(e, "published");
        _group.ExecuteNonQuery();
    }

    private void InsertDogmaAttribute(JsonElement e)
    {
        _dogmaAttribute.Parameters["$attributeId"].Value = Key(e);
        _dogmaAttribute.Parameters["$name"].Value = Str(e, "name");
        _dogmaAttribute.Parameters["$displayNameEn"].Value = NullableEnName(e, "displayName");
        _dogmaAttribute.Parameters["$defaultValue"].Value = Double(e, "defaultValue");
        _dogmaAttribute.Parameters["$stackable"].Value = Bool(e, "stackable");
        _dogmaAttribute.Parameters["$highIsGood"].Value = Bool(e, "highIsGood");
        _dogmaAttribute.Parameters["$unitId"].Value = NullableInt(e, "unitID");
        _dogmaAttribute.Parameters["$published"].Value = Bool(e, "published");
        _dogmaAttribute.Parameters["$maxAttributeId"].Value = NullableInt(e, "maxAttributeID");
        _dogmaAttribute.ExecuteNonQuery();
    }

    private void InsertDogmaEffect(JsonElement e)
    {
        _dogmaEffect.Parameters["$effectId"].Value = Key(e);
        _dogmaEffect.Parameters["$name"].Value = Str(e, "name");
        _dogmaEffect.Parameters["$effectCategoryId"].Value = Int(e, "effectCategoryID");
        _dogmaEffect.Parameters["$published"].Value = Bool(e, "published");
        _dogmaEffect.Parameters["$modifierInfoJson"].Value =
            e.TryGetProperty("modifierInfo", out var mi) && mi.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? mi.GetRawText()
                : DBNull.Value;
        _dogmaEffect.ExecuteNonQuery();
    }

    private void InsertType(JsonElement e)
    {
        var name = EnName(e, "name");
        _type.Parameters["$typeId"].Value = Key(e);
        _type.Parameters["$groupId"].Value = Int(e, "groupID");
        _type.Parameters["$nameEn"].Value = name;
        _type.Parameters["$nameKey"].Value = SqliteSdeAccessor.NameKey(name);
        _type.Parameters["$published"].Value = Bool(e, "published");
        _type.Parameters["$mass"].Value = Double(e, "mass");
        _type.Parameters["$volume"].Value = Double(e, "volume");
        _type.Parameters["$capacity"].Value = Double(e, "capacity");
        _type.Parameters["$marketGroupId"].Value = NullableInt(e, "marketGroupID");
        _type.ExecuteNonQuery();
        _WriteNameAliases(Key(e), e, name);
    }

    // Locale-agnostic name import: for every non-English locale on the type's `name` object, store a
    // (typeId, lowercased name, locale) alias so an EFT-fit with localized names resolves to the same typeId. The
    // English name stays canonical on Type.nameKey; a localized name equal to English is skipped (already covered).
    private void _WriteNameAliases(long typeId, JsonElement e, string englishName)
    {
        if (!e.TryGetProperty("name", out var nameObject) || nameObject.ValueKind != JsonValueKind.Object)
            return;
        var englishKey = SqliteSdeAccessor.NameKey(englishName);
        foreach (var locale in nameObject.EnumerateObject())
        {
            if (locale.NameEquals("en") || locale.Value.ValueKind != JsonValueKind.String)
                continue;
            var localized = locale.Value.GetString();
            if (string.IsNullOrWhiteSpace(localized))
                continue;
            var key = SqliteSdeAccessor.NameKey(localized);
            if (key == englishKey)
                continue;
            _typeAlias.Parameters["$typeId"].Value = typeId;
            _typeAlias.Parameters["$nameKey"].Value = key;
            _typeAlias.Parameters["$locale"].Value = locale.Name;
            _typeAlias.ExecuteNonQuery();
        }
    }

    private void InsertTypeDogma(JsonElement e)
    {
        var typeId = Key(e);
        var slot = SdeSlotType.None;
        var isLauncher = false;
        var isTurret = false;
        double numberOfSlots = 0;

        if (e.TryGetProperty("dogmaAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
        {
            foreach (var attr in attributes.EnumerateArray())
            {
                var attributeId = attr.GetProperty("attributeID").GetInt32();
                var value = attr.GetProperty("value").GetDouble();
                _typeDogmaAttribute.Parameters["$typeId"].Value = typeId;
                _typeDogmaAttribute.Parameters["$attributeId"].Value = attributeId;
                _typeDogmaAttribute.Parameters["$value"].Value = value;
                _typeDogmaAttribute.ExecuteNonQuery();
                if (attributeId == SlotEffects.SlotsConsumedAttribute)
                    numberOfSlots = value;
            }
        }

        if (e.TryGetProperty("dogmaEffects", out var effects) && effects.ValueKind == JsonValueKind.Array)
        {
            foreach (var effect in effects.EnumerateArray())
            {
                var effectId = effect.GetProperty("effectID").GetInt32();
                var isDefault = effect.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True;
                _typeDogmaEffect.Parameters["$typeId"].Value = typeId;
                _typeDogmaEffect.Parameters["$effectId"].Value = effectId;
                _typeDogmaEffect.Parameters["$isDefault"].Value = isDefault;
                _typeDogmaEffect.ExecuteNonQuery();

                var mapped = SlotEffects.ToSlotType(effectId);
                if (mapped != SdeSlotType.None)
                    slot = mapped;
                if (effectId == SlotEffects.LauncherFitted)
                    isLauncher = true;
                if (effectId == SlotEffects.TurretFitted)
                    isTurret = true;
            }
        }

        // Only fittable modules (those occupying a slot) get a requirement row — the parser's "is this a module?" gate.
        if (slot == SdeSlotType.None)
            return;
        _fitRequirement.Parameters["$typeId"].Value = typeId;
        _fitRequirement.Parameters["$slotType"].Value = (int)slot;
        _fitRequirement.Parameters["$numberOfSlots"].Value = (int)numberOfSlots;
        _fitRequirement.Parameters["$isLauncher"].Value = isLauncher;
        _fitRequirement.Parameters["$isTurret"].Value = isTurret;
        _fitRequirement.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(
        SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter, SqliteType.Text);
        command.Prepare();
        return command;
    }

    private static long Key(JsonElement e) => e.GetProperty("_key").GetInt64();

    private static int Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static object NullableInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : DBNull.Value;

    private static double Double(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0d;

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string EnName(JsonElement e, string prop) => NullableEnName(e, prop) as string ?? string.Empty;

    private static object NullableEnName(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v))
            return DBNull.Value;
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? (object)DBNull.Value;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
            return en.GetString() ?? (object)DBNull.Value;
        return DBNull.Value;
    }
}
