namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Pure (no-SDE) parser for the compact DNA fitting string used by in-game fitting links / clipboard:
/// <c>shipTypeId:typeId;qty:typeId;qty::</c>. The leading token is the hull; each remaining <c>typeId;qty</c>
/// is an item. DNA does not pair charges with modules, so charges land in cargo at assembly time.
/// </summary>
internal static class DnaFitParser
{
    public static RawFit? Parse(string text)
    {
        var trimmed = text.Trim();
        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var shipTypeId))
            return null;

        var items = new List<RawFitItem>();
        foreach (var token in parts.Skip(1))
        {
            var segments = token.Split(';');
            if (!int.TryParse(segments[0], out var typeId))
                continue;
            var quantity = segments.Length > 1 && int.TryParse(segments[1], out var q) && q > 0 ? q : 1;
            items.Add(new RawFitItem(Name: null, typeId, quantity, ChargeName: null));
        }

        return new RawFit(ShipName: null, shipTypeId, "Imported fit", items);
    }
}
