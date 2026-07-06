using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Fittings;

/// <summary>
/// Content identity of a fit (2026-06-04): the order-independent fingerprint used to dedup fits across ESI import,
/// share-to-server and download — regardless of owner or ESI fitting id. The approach: collect every type id —
/// the hull plus each item repeated by its quantity —
/// sort ascending, and MD5 the result. Item order and quantity grouping (one line "x4" vs four lines "x1") therefore
/// do not change the hash; module slot, fit name and description are ignored (a renamed identical fit is the same fit).
/// </summary>
public static class FitContentHash
{
    /// <summary>The content fingerprint of a fit from its raw ESI JSON (hex MD5). Deterministic; unparseable JSON
    /// falls back to hashing the raw text so a value is always produced.</summary>
    public static string Compute(string rawJson)
    {
        var typeIds = new List<int>();
        try
        {
            var fit = JsonSerializer.Deserialize<EsiFitting>(rawJson);
            if (fit is not null)
            {
                typeIds.Add(fit.ShipTypeId);
                foreach (var item in fit.Items ?? [])
                    for (var i = 0; i < item.Quantity; i++)
                        typeIds.Add(item.TypeId);
            }
        }
        catch (JsonException)
        {
            return HashOf(rawJson);
        }

        typeIds.Sort();
        return HashOf(string.Join(",", typeIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
    }

    private static string HashOf(string canonical) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
