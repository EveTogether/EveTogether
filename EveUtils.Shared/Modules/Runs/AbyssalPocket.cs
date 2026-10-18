using System.Globalization;

namespace EveUtils.Shared.Modules.Runs;

/// <summary>The tier and weather stored on an abyssal run (ET-241), as <c>RunParameterKey.AbyssalFilament</c>'s
/// <c>"{tier index}|{weather name}"</c>. One reading for the screens and the earnings key (ET-513).</summary>
public static class AbyssalPocket
{
    /// <summary>The stored tier index and weather name, or null where nothing (readable) was stored.</summary>
    public static (int Tier, string Weather)? Parse(string? typedValue) =>
        typedValue?.Split('|') is [{ } tierText, { Length: > 0 } weatherName]
        && int.TryParse(tierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier)
            ? (tier, weatherName)
            : null;
}
