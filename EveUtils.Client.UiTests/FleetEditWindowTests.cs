using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class FleetEditWindowTests
{
    [AvaloniaFact]
    public void EditFleet_DatePickersDoNotOverlapActions()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        var window = new FleetEditWindow(new FleetInfo(
            1,
            "Op Alpha",
            "Fleet description",
            FleetVisibility.Public,
            FleetState.Active,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow,
            default));

        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        CalendarDatePicker from = window.FindControl<CalendarDatePicker>("FromDate")
            ?? throw new InvalidOperationException("FromDate was not found");
        CalendarDatePicker to = window.FindControl<CalendarDatePicker>("ToDate")
            ?? throw new InvalidOperationException("ToDate was not found");
        var cancel = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "Cancel");
        Button confirm = window.FindControl<Button>("ConfirmButton")
            ?? throw new InvalidOperationException("ConfirmButton was not found");

        double datesBottom = Math.Max(_Bottom(from), _Bottom(to));
        double actionsTop = Math.Min(_Top(cancel), _Top(confirm));
        Assert.True(datesBottom <= actionsTop, $"date pickers end at {datesBottom:F1}, actions start at {actionsTop:F1}");

        window.Close();

        double _Top(Control control) => (control.TranslatePoint(default, window) ?? default).Y;
        double _Bottom(Control control) => _Top(control) + control.Bounds.Height;
    }
}
