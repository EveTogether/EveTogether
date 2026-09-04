using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-163 AC-6: the module host gives a docked module 758px (measured in the ET-156 mockup); the same
/// content has to work at a wide floating width too — <see cref="ModuleHostService"/> moves the very same
/// <c>Content</c> between the two, so there is only one layout to get right.</summary>
public class ManualRunStartLayoutTests
{
    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating => false;
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    // AC-6's counterproof: render at the module host's own 758px docked width and at a wide 1180 floating width —
    // no field or button may fall outside either.
    [AvaloniaTheory]
    [InlineData(758)]
    [InlineData(1180)]
    public void Form_FitsWithoutOverflowingItsWidth(double width)
    {
        using var instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()));
        var vm = new ManualRunStartViewModel(instance.Services.GetRequiredService<ICqrsDispatcher>(),
            instance.Services.GetRequiredService<ISdeAccessor>(), []);
        var window = new ManualRunStartWindow(vm) { Width = width, Height = 560 };

        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "START RUN", "runs-start", "runs-start");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 560, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        // Every field and button the form actually offers — not every template-internal part (a ComboBox's own
        // dropdown-glyph icon reports a bogus multi-thousand-pixel Viewbox size under the headless renderer, which
        // is a measurement artifact of that glyph's template and not a real overflow of the form itself).
        var fieldsAndButtons = content.GetVisualDescendants()
            .Where(c => c is Button or TextBox or ComboBox or ListBox or ToggleButton or DatePicker or TimePicker)
            .OfType<Control>().Where(c => c.IsVisible);

        foreach (Control control in fieldsAndButtons)
        {
            Point topLeft = control.TranslatePoint(default, root) ?? default;
            Assert.True(topLeft.X + control.Bounds.Width <= root.Bounds.Width + 0.5,
                $"{control.GetType().Name} overflows at width {width}: right edge " +
                $"{topLeft.X + control.Bounds.Width:F1} > {root.Bounds.Width}");
        }
    }
}
