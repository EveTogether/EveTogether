using System.Globalization;

namespace EveUtils.Server.DataExplorer;

public static class IskText
{
    /// <summary>Whole ISK with thousands separators, the way the game prints a bounty: "1,250,000 ISK".</summary>
    public static string Format(decimal isk) => $"{isk.ToString("N0", CultureInfo.InvariantCulture)} ISK";
}
