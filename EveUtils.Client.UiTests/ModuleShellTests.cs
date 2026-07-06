using System.IO;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Headless render + behaviour checks for the module shell: the rail + character column + host layout,
/// the dock/float and collapse axes (which resize the window to the narrow shell / rail-only) and the faction
/// theming re-tinting the rendered surface. PNGs are saved so each state can be eyeballed without launching the app.
/// </summary>
public class ModuleShellTests
{
    private static string Out(string name) => Path.Combine(Path.GetTempPath(), name);

    private static MainWindowViewModel PopulatedVm()
    {
        var vm = new MainWindowViewModel();
        vm.Characters.Add(new CharacterViewModel(new Character("Jithran")) { Affiliation = "Imperial Academy [IAC]" });
        vm.Characters.Add(new CharacterViewModel(new Character("Lyra Custos")) { Affiliation = "Iron Souls Corp [TIS]" });
        return vm;
    }

    private static MainWindow Show(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Docked_Default_RendersFullShell()
    {
        var vm = PopulatedVm();
        var window = Show(vm);

        Assert.True(vm.ShowHost);
        Assert.True(vm.ShowChars);
        Assert.True(vm.ShowHeaderWindowButtons);
        Assert.False(vm.ShowRailWindowButtons);
        Assert.Equal(1100d, window.Width);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Out("eveutils-shell-docked.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void DockedCollapsed_HidesChars_KeepsHostWide()
    {
        var vm = PopulatedVm();
        vm.IsCharsCollapsed = true;
        var window = Show(vm);

        Assert.False(vm.ShowChars);     // character column hidden
        Assert.True(vm.ShowHost);       // host stays visible (collapse is independent of dock mode)
        Assert.Equal(1100d, window.Width);

        var frame = window.CaptureRenderedFrame();
        frame!.Save(Out("eveutils-shell-docked-collapsed.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Floating_ShrinksToNarrowShell()
    {
        var vm = PopulatedVm();
        vm.IsFloating = true;
        var window = Show(vm);

        Assert.False(vm.ShowHost);                 // host hidden in floating mode
        Assert.True(vm.ShowChars);                 // rail + characters remain
        Assert.False(vm.ShowMaximizeButton);       // maximize pointless on the narrow shell
        // Floating keeps the min/close in the rail bottom (not the header) regardless of the character column, so
        // the rail bottom does not jump when toggling characters on/off.
        Assert.True(vm.ShowRailWindowButtons);
        Assert.False(vm.ShowHeaderWindowButtons);
        Assert.Equal("FLOATING", vm.DockModeLabel);
        Assert.Equal(360d, window.Width);

        var frame = window.CaptureRenderedFrame();
        frame!.Save(Out("eveutils-shell-floating.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void FloatingCollapsed_IsRailOnly_WithRailWindowControls()
    {
        var vm = PopulatedVm();
        vm.IsFloating = true;
        vm.IsCharsCollapsed = true;
        var window = Show(vm);

        Assert.False(vm.ShowChars);
        Assert.False(vm.ShowHeaderWindowButtons);  // header controls hidden
        Assert.True(vm.ShowRailWindowButtons);     // min/close moved into the rail
        Assert.True(vm.CenterBrand);               // hex logo centred
        Assert.Equal(92d, window.Width);           // rail-only matches the rail width (no sliver)

        var frame = window.CaptureRenderedFrame();
        frame!.Save(Out("eveutils-shell-rail-only.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Floating_RemembersDockedWidth_AcrossToggle()
    {
        var vm = PopulatedVm();
        var window = Show(vm);                 // docked, default 1100

        window.Width = 1300;                   // user resizes the docked shell
        Dispatcher.UIThread.RunJobs();

        vm.IsFloating = true;                  // → floating narrow shell
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(360d, window.Width);

        vm.IsFloating = false;                 // → docked restores the remembered width (not reset to default)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1300d, window.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void FactionThemes_ReTint_TheRenderedShell()
    {
        var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        var vm = PopulatedVm();
        var window = Show(vm);

        try
        {
            theme.Apply(FactionTheme.Gallente);
            Dispatcher.UIThread.RunJobs();
            var gallente = Out("eveutils-shell-gallente.png");
            window.CaptureRenderedFrame()!.Save(gallente);

            theme.Apply(FactionTheme.Amarr);
            Dispatcher.UIThread.RunJobs();
            var amarr = Out("eveutils-shell-amarr.png");
            window.CaptureRenderedFrame()!.Save(amarr);

            theme.Apply(FactionTheme.Caldari);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Out("eveutils-shell-caldari.png"));

            theme.Apply(FactionTheme.Minmatar);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Out("eveutils-shell-minmatar.png"));

            // The live swap must actually change pixels (DynamicResource re-tint), not just the resource value.
            Assert.False(File.ReadAllBytes(gallente).AsSpan().SequenceEqual(File.ReadAllBytes(amarr)));
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
            window.Close();
            instance.Dispose();
        }
    }

    [AvaloniaFact]
    public void Toggles_DockMode_And_Chars()
    {
        var vm = PopulatedVm();

        Assert.Null(vm.ActiveModule);               // home → no rail item highlighted (rail-launch coverage lives in ModuleNavigationTests)
        Assert.False(vm.IsFitsActive);

        vm.ToggleDockModeCommand.Execute(null);
        Assert.True(vm.IsFloating);
        Assert.Equal("DOCK", vm.DockToggleLabel);   // rail toggle now invites docking again
        vm.ToggleDockModeCommand.Execute(null);     // and floating → docked round-trips (regression: was a dead end)
        Assert.False(vm.IsFloating);
        Assert.Equal("FLOAT", vm.DockToggleLabel);

        vm.ToggleCharsCommand.Execute(null);
        Assert.True(vm.IsCharsCollapsed);
    }
}
