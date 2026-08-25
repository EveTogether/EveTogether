using System.Globalization;

namespace EveUtils.Client.Formatting;

/// <summary>
/// Pins the process to the invariant culture, so every readout formats identically on every machine. The app ships
/// one English UI with no localisation, and the formatting helpers around it already pass
/// <see cref="CultureInfo.InvariantCulture"/> explicitly; this closes the gap for plain interpolation.
/// </summary>
public static class ClientCulture
{
    /// <summary>
    /// Call once at start-up, before anything formats a number.
    /// </summary>
    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
