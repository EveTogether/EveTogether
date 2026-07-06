using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace EveUtils.Client.Views;

/// <summary>
/// A window with the same borderless EVE chrome as the main window: no OS frame, a custom titlebar
/// (drag + minimize/close) and square corners + edge resize. Feature windows that pop out in floating mode derive
/// from this so they match the shell. The chrome is a code-built control template (set as a local value so it wins
/// over the inherited Window theme) whose titlebar lives in the template — not the content — so re-hosting a window's
/// content as a docked tab never grabs the chrome. The brushes bind to resource observables so a faction swap still
/// re-tints the chrome live. A VisualLayerManager keeps tooltips / combo popups / context menus working.
/// </summary>
public class ChromedWindow : Window
{
    // The EVE Together badge, shared by every chromed titlebar; loaded once (the asset cannot change at runtime).
    private static readonly Bitmap BrandBadge = new(AssetLoader.Open(new Uri("avares://EveUtils.Client/Assets/eve-together-badge.png")));

    // The app icon, so a floating/popped-out chromed window shows the EVE Together icon in the taskbar (matching the
    // main window) instead of the default Avalonia icon. Loaded once.
    private static readonly WindowIcon AppIcon = new(AssetLoader.Open(new Uri("avares://EveUtils.Client/Assets/eve-together.ico")));

    public ChromedWindow()
    {
        // Visually borderless, but on Windows via extend-client-area so native snap survives (see
        // WindowChrome.ConfigureBorderless — derived XAMLs no longer set WindowDecorations themselves). The
        // code-built template below provides our chrome as a local value so it wins over the inherited Window theme.
        WindowChrome.ConfigureBorderless(this);
        Icon = AppIcon;
        Template = new FuncControlTemplate<ChromedWindow>(BuildChrome);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.ApplySquareCorners(this);             // angular EVE look — opt out of Windows 11 corner rounding
        if (CanResize) WindowChrome.EnableBorderlessResize(this);   // only resizable windows get edge/corner resize
    }

    private static Control BuildChrome(ChromedWindow window, INameScope scope)
    {
        var badge = new Image { Height = 22, Source = BrandBadge, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(badge, BitmapInterpolationMode.HighQuality);

        var title = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 12, LetterSpacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
        title[!TextBlock.TextProperty] = new Binding(nameof(Title)) { Source = window };
        title.Bind(TextBlock.ForegroundProperty, window.GetResourceObservable("TextBrightBrush"));

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(badge);
        brand.Children.Add(title);

        var minimize = new Button { Content = "—" };
        minimize.Classes.Add("winbtn");
        ToolTip.SetTip(minimize, "Minimize");
        minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
        // Fixed-size dialogs don't minimize (they're not resizable); only the resizable module pop-outs do.
        minimize.Bind(Visual.IsVisibleProperty, window.GetObservable(CanResizeProperty));

        var close = new Button { Content = "✕" };
        close.Classes.Add("winbtn");
        close.Classes.Add("winclose");
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => window.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(buttons, 1);
        buttons.Children.Add(minimize);
        buttons.Children.Add(close);

        var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        barGrid.Children.Add(brand);
        barGrid.Children.Add(buttons);

        var titleBar = new Border { Height = 40, Child = barGrid };
        titleBar.Classes.Add("titlebar");
        titleBar.PointerPressed += (_, e) => OnTitleBarPressed(window, e);
        titleBar.DoubleTapped += (_, _) => ToggleMaximize(window);
        DockPanel.SetDock(titleBar, Dock.Top);

        // Windows snap (no-op unless the client area is extended): the bar is a native caption drag area — that is
        // what makes drag-to-edge snap work — and the buttons stay clickable on top of it.
        WindowDecorationProperties.SetElementRole(titleBar, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(minimize, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(close, WindowDecorationsElementRole.User);

        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter[!ContentPresenter.ContentProperty] = new Binding(nameof(Content)) { Source = window };

        var body = new DockPanel();
        body.Children.Add(titleBar);
        body.Children.Add(presenter);

        var root = new Border { BorderThickness = new Thickness(1), Child = new VisualLayerManager { Child = body } };
        root.Bind(Border.BackgroundProperty, window.GetResourceObservable("WindowBackgroundBrush"));
        root.Bind(Border.BorderBrushProperty, window.GetResourceObservable("BorderStrongBrush"));
        return root;
    }

    private static void OnTitleBarPressed(ChromedWindow window, PointerPressedEventArgs e)
    {
        // Let the window-control buttons handle their own clicks; otherwise the whole bar drags the window.
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
        if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
    }

    private static void ToggleMaximize(ChromedWindow window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
