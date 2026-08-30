using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>Persisted geometry/opacity for a pop-out overlay: where it was, how big, how see-through, pinned or not.</summary>
internal sealed class OverlayGeometry
{
    public bool HasPosition { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Opacity { get; set; } = 0.9;
    public bool Pinned { get; set; } = true;
}

/// <summary>
/// Loads/saves an overlay's geometry via the Settings module (one key per overlay), so a pop-out reopens where you
/// left it. Backed by the client SQLite settings store.
///
/// Keyed by an opaque string rather than by a character, because "remembering where a pop-out sat" is the same job
/// whether the pop-out is one pilot's DPS meter or a whole fleet's readout — see <see cref="ForCharacter"/> and
/// <see cref="ForFleet"/> for the two key shapes in use.
/// </summary>
internal static class OverlayGeometryStore
{
    /// <summary>The per-character DPS pop-out's key. Unchanged from before the fleet overlay existed, so nobody's
    /// remembered window position moves when they update.</summary>
    public static string ForCharacter(string character) => $"ui.dps-overlay.{character.ToLowerInvariant()}";

    /// <summary>The fleet overlay's key: per fleet, because where you want a fleet's readout is a property of that
    /// op, and two fleets open at once are two windows that must not fight over one position.</summary>
    public static string ForFleet(long fleetId) => $"ui.fleet-overlay.{fleetId}";

    public static async Task<OverlayGeometry?> LoadAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        IServiceProvider? provider = Program.Services;
        if (provider is null) return null; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery());
        var value = settings.FirstOrDefault(s => s.Key == key)?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        try { return JsonSerializer.Deserialize<OverlayGeometry>(value); }
        catch (JsonException) { return null; }
    }

    public static async Task SaveAsync(string key, OverlayGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        IServiceProvider? provider = Program.Services;
        if (provider is null) return; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(key, JsonSerializer.Serialize(geometry)));
    }
}
