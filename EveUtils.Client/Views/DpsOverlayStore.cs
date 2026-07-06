using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>Persisted geometry/opacity for a per-character DPS overlay.</summary>
internal sealed class DpsOverlayGeometry
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
/// Loads/saves DPS-overlay geometry per character via the Settings module (one key per character), so a
/// popped-out overlay reopens where you left it. Backed by the client SQLite settings store.
/// </summary>
internal static class DpsOverlayStore
{
    private static string Key(string character) => $"ui.dps-overlay.{character.ToLowerInvariant()}";

    public static async Task<DpsOverlayGeometry?> LoadAsync(string character)
    {
        if (string.IsNullOrWhiteSpace(character)) return null;

        IServiceProvider? provider = Program.Services;
        if (provider is null) return null; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery());
        var value = settings.FirstOrDefault(s => s.Key == Key(character))?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        try { return JsonSerializer.Deserialize<DpsOverlayGeometry>(value); }
        catch (JsonException) { return null; }
    }

    public static async Task SaveAsync(string character, DpsOverlayGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(character)) return;

        IServiceProvider? provider = Program.Services;
        if (provider is null) return; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(Key(character), JsonSerializer.Serialize(geometry)));
    }
}
