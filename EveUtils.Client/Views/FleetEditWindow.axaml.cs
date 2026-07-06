using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Views;

/// <summary>
/// Create/edit-fleet dialog. Returns a <see cref="FleetEditResult"/> on confirm, null on cancel. Values are
/// set + read in code-behind (not ElementName bindings — those fail after Load(), see MessageBoxWindow). Passing
/// an existing fleet pre-fills the fields and switches the labels to "edit".
/// </summary>
public partial class FleetEditWindow : ChromedWindow
{
    public FleetEditWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetEditWindow(FleetInfo existing) : this()
    {
        Title = "Edit fleet";
        this.FindControl<TextBlock>("HeaderBlock")!.Text = "Edit fleet";
        this.FindControl<Button>("ConfirmButton")!.Content = "Save →";
        this.FindControl<TextBox>("NameBox")!.Text = existing.Name;
        this.FindControl<TextBox>("DescriptionBox")!.Text = existing.Description ?? string.Empty;
        this.FindControl<ComboBox>("VisibilityBox")!.SelectedIndex = (int)existing.Visibility;
        this.FindControl<CalendarDatePicker>("FromDate")!.SelectedDate = existing.FromTime?.LocalDateTime;
        this.FindControl<CalendarDatePicker>("ToDate")!.SelectedDate = existing.ToTime?.LocalDateTime;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NameBox")?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return; // name is required — keep the dialog open

        var description = this.FindControl<TextBox>("DescriptionBox")?.Text?.Trim();
        var visibility = (FleetVisibility)(this.FindControl<ComboBox>("VisibilityBox")?.SelectedIndex ?? 0);
        var from = this.FindControl<CalendarDatePicker>("FromDate")?.SelectedDate;
        var to = this.FindControl<CalendarDatePicker>("ToDate")?.SelectedDate;

        Close(new FleetEditResult(
            name,
            string.IsNullOrWhiteSpace(description) ? null : description,
            visibility,
            from is { } f ? new DateTimeOffset(f) : null,
            to is { } t ? new DateTimeOffset(t) : null));
    }
}
