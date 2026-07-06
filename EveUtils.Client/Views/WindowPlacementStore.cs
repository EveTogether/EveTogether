using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>Persisted placement of the main window so it reopens the way the user left it. Width is per shell mode
/// (docked vs floating; rail-only is fixed and not remembered), height is shared, plus whether it was maximized and
/// its last Normal-state screen position. The position is only restored if it still lands on a connected monitor
/// (otherwise the window centres on an available screen — guards against a removed/rearranged second monitor).</summary>
internal sealed class WindowPlacement
{
    public double DockedWidth { get; set; }
    public double FloatingWidth { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public bool HasPosition { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// Loads/saves the main window's size via the Settings module (one key), so the window resumes its previous size
/// on the next launch. Backed by the client SQLite settings store; a no-op before the service provider exists.
/// </summary>
internal static class WindowPlacementStore
{
    private const string Key = "ui.main-window";

    public static async Task<WindowPlacement?> LoadAsync()
    {
        IServiceProvider? provider = Program.Services;
        if (provider is null) return null; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var settings = await dispatcher.Query(new GetSettingsQuery());
        var value = settings.FirstOrDefault(s => s.Key == Key)?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        try { return JsonSerializer.Deserialize<WindowPlacement>(value); }
        catch (JsonException) { return null; }
    }

    public static async Task SaveAsync(WindowPlacement placement)
    {
        IServiceProvider? provider = Program.Services;
        if (provider is null) return; // headless tests / pre-bootstrap: no persistence

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await dispatcher.Send(new SetSettingCommand(Key, JsonSerializer.Serialize(placement)));
    }
}
