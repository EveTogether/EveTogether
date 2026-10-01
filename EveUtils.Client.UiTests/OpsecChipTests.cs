using Avalonia.Headless.XUnit;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-417: the title bar says OPSEC is on for exactly as long as it is, whichever way it was switched.</summary>
public sealed class OpsecChipTests
{
    [AvaloniaFact]
    public async Task Chip_FollowsOpsec_LiveBothWays()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        var shell = new MainWindowViewModel(instance.Services);
        var opsec = instance.Services.GetRequiredService<IOpsecService>();
        Assert.False(shell.IsOpsecOn);

        await opsec.SetEnabledAsync(true);
        Assert.True(shell.IsOpsecOn);
        Assert.Contains("Ctrl+Shift+O", shell.OpsecChipTooltip);

        await opsec.SetEnabledAsync(false);
        Assert.False(shell.IsOpsecOn);
    }

    [AvaloniaFact]
    public async Task Chip_StartsOn_WhenOpsecWasLeftOn()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        var opsec = instance.Services.GetRequiredService<IOpsecService>();
        await opsec.SetEnabledAsync(true);

        var shell = new MainWindowViewModel(instance.Services);

        Assert.True(shell.IsOpsecOn);
    }
}
