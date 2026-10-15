using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-508: the run window showing a freerun.</summary>
public sealed class FreerunWindowTests
{
    /// <summary>AC-7, on a window already showing the freerun: neither a copied signature nor a copied mission stops it
    /// or waits to take its place, and the freerun keeps its own name. Red while a copy stops the run to wait.</summary>
    [AvaloniaFact]
    public async Task ACopiedSignatureOrMission_LeavesTheFreerunRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync(ActivityKind.Free);
        await window.StartRunCommand.ExecuteAsync(null);
        string? freerunName = window.SignatureName;

        window.ApplySignature("AAA-001", "Combat Site", "Haunted Yard", []);
        await window.LastSignature;
        window.ApplyMission(3008416, 4, null, "Some Agent", []);
        await window.LastMission;

        Assert.Equal(ActivityRunState.Running, window.RunState);
        Assert.StartsWith("Freerun ", freerunName);
        Assert.Equal(freerunName, window.SignatureName);
        window.Dispose();
    }
}
