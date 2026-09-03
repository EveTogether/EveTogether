using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Data.Sqlite;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Prepared bulk-insert commands for the SDE tables, reused row-by-row inside the build transaction. Owns the
/// per-dataset JSON shape: types/groups/categories carry a localized <c>name.en</c>, dogma attributes/effects a
/// plain <c>name</c> string, and typeDogma drives both the per-type dogma rows and the pre-computed slot table.
/// archetypes/factions/typeLists write no rows of their own — they accumulate into lookups that the later
/// dungeons pass denormalises onto each Site row (hence the dataset order in <see cref="SdeSqliteBuilder"/>).
/// </summary>
internal sealed partial class TableWriters
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
    private readonly SqliteCommand _site;
    private readonly SqliteCommand _siteAlias;

    private readonly Dictionary<long, string> _archetypeNames = [];
    private readonly Dictionary<long, string> _factionNames = [];
    private readonly Dictionary<long, int[]> _shipGroupsByTypeList = [];

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
        _site = Prepare(connection, transaction,
            "INSERT INTO Site (dungeonId, nameEn, archetypeId, archetypeName, factionId, factionName, description, dedRating, shipGroupIdsJson) " +
            "VALUES ($dungeonId, $nameEn, $archetypeId, $archetypeName, $factionId, $factionName, $description, $dedRating, $shipGroupIdsJson);",
            "$dungeonId", "$nameEn", "$archetypeId", "$archetypeName", "$factionId", "$factionName", "$description",
            "$dedRating", "$shipGroupIdsJson");
        _siteAlias = Prepare(connection, transaction,
            "INSERT INTO SiteNameAlias (dungeonId, nameKey, locale) VALUES ($dungeonId, $nameKey, $locale);",
            "$dungeonId", "$nameKey", "$locale");
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
            case "archetypes.jsonl": CollectArchetype(element); break;
            case "factions.jsonl": CollectFaction(element); break;
            case "typeLists.jsonl": CollectTypeList(element); break;
            case "dungeons.jsonl": InsertSite(element); break;
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

    private void CollectArchetype(JsonElement e)
    {
        // Archetype 43 (45 dungeons) carries a description but no title at all — it stays absent here and the
        // Site row keeps a null archetypeName rather than an invented one.
        if (NullableEnName(e, "title") is string title && title.Length > 0)
            _archetypeNames[Key(e)] = title;
    }

    private void CollectFaction(JsonElement e)
    {
        if (NullableEnName(e, "name") is string name && name.Length > 0)
            _factionNames[Key(e)] = name;
    }

    private void CollectTypeList(JsonElement e) => _shipGroupsByTypeList[Key(e)] = IntArray(e, "includedGroupIDs");

    private void InsertSite(JsonElement e)
    {
        var description = StripHtml(EnName(e, "description"));
        var archetypeId = NullableInt(e, "archetypeID");
        var factionId = NullableInt(e, "factionID");
        var dungeonId = Key(e);
        var name = EnName(e, "name");

        _site.Parameters["$dungeonId"].Value = dungeonId;
        _site.Parameters["$nameEn"].Value = name;
        _site.Parameters["$archetypeId"].Value = archetypeId;
        _site.Parameters["$archetypeName"].Value = Lookup(_archetypeNames, archetypeId);
        _site.Parameters["$factionId"].Value = factionId;
        _site.Parameters["$factionName"].Value = Lookup(_factionNames, factionId);
        _site.Parameters["$description"].Value = description.Length > 0 ? description : DBNull.Value;
        _site.Parameters["$dedRating"].Value = DedRating(description);
        _site.Parameters["$shipGroupIdsJson"].Value = ShipGroupIdsJson(e);
        _site.ExecuteNonQuery();
        _WriteSiteNameAliases(dungeonId, e);
    }

    // Unlike _WriteNameAliases (which skips "en" because Type carries its own persisted, .NET-normalised nameKey
    // column), this writes every locale including English. Site has no nameKey column of its own, and SQLite's
    // LOWER() is ASCII-only, so comparing a lookup key against nameEn in SQL would not agree with .NET's
    // ToLowerInvariant() for the handful of English names that carry a non-ASCII character (measured: 4 of 1409
    // dungeons, build 3492266, e.g. "Salvation Angel´s Shipment"). Routing every locale through this table keeps
    // both sides of the comparison normalised the same way.
    private void _WriteSiteNameAliases(long dungeonId, JsonElement e)
    {
        if (!e.TryGetProperty("name", out var nameObject) || nameObject.ValueKind != JsonValueKind.Object)
            return;
        foreach (var locale in nameObject.EnumerateObject())
        {
            if (locale.Value.ValueKind != JsonValueKind.String)
                continue;
            var localized = locale.Value.GetString();
            if (string.IsNullOrWhiteSpace(localized))
                continue;
            _siteAlias.Parameters["$dungeonId"].Value = dungeonId;
            _siteAlias.Parameters["$nameKey"].Value = SqliteSdeAccessor.NameKey(localized);
            _siteAlias.Parameters["$locale"].Value = locale.Name;
            _siteAlias.ExecuteNonQuery();
        }
    }

    // NULL when the dungeon carries no allowedShipsList, a JSON array of InvGroup ids when it does. The array can be
    // empty: 6 of the 70 referenced type lists (touching 27 dungeons) express their allow-list as includedTypeIDs or
    // as a display-only description instead of ship groups. Keeping NULL and "[]" distinct stops those 27 restricted
    // sites from reading as unrestricted.
    // ponytail: includedGroupIDs only — the 9 includedTypeIDs / 7 excludedTypeIDs refinements are a known ceiling;
    // widen to full set algebra if a consumer needs per-hull precision.
    private object ShipGroupIdsJson(JsonElement e)
    {
        if (!e.TryGetProperty("allowedShipsList", out var lists) || lists.ValueKind != JsonValueKind.Array)
            return DBNull.Value;
        var groups = new SortedSet<int>();
        var any = false;
        foreach (var list in lists.EnumerateArray())
        {
            if (list.ValueKind != JsonValueKind.Number)
                continue;
            any = true;
            if (_shipGroupsByTypeList.TryGetValue(list.GetInt64(), out var ids))
                groups.UnionWith(ids);
        }
        return any ? "[" + string.Join(",", groups) + "]" : DBNull.Value;
    }

    private static object Lookup(Dictionary<long, string> names, object id) =>
        id is int key && names.TryGetValue(key, out var name) ? name : DBNull.Value;

    // 38 of the 1409 descriptions carry a parsable rating; the term appears 42 times, so anchoring on the phrase
    // alone would invent a rating for the four that omit it.
    private static object DedRating(string description)
    {
        var match = DedThreatAssessment().Match(description);
        return match.Success ? int.Parse(match.Groups[1].Value) : DBNull.Value;
    }

    [GeneratedRegex(@"DED Threat Assessment[^(]*\((\d{1,2}) of 10\)", RegexOptions.IgnoreCase)]
    private static partial Regex DedThreatAssessment();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    // Site descriptions are HTML (<p>, </p>, <br>). Strip once at import so no consumer has to. Tags become a space
    // so paragraphs do not run together, then whitespace collapses.
    private static string StripHtml(string value) =>
        value.Length == 0 ? value : WhitespaceRun().Replace(WebUtility.HtmlDecode(HtmlTag().Replace(value, " ")), " ").Trim();

    private static int[] IntArray(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<int>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number)
                result.Add(item.GetInt32());
        return [.. result];
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
