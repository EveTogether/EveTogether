using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = new MainWindowViewModel(Program.Services);
            var mainWindow = new MainWindow
            {
                // ViewModel from the composition root → gets the IDispatcher via DI.
                DataContext = mainViewModel,
            };

            // The dialog service shows modals over the main window and routes non-modal feature views to the docked
            // module host when docked, or to separate windows when floating.
            var dialogs = Program.Services.GetRequiredService<DialogService>();
            dialogs.SetOwner(mainWindow);
            dialogs.SetHost(mainViewModel);

            // apply the persisted faction theme (default Gallente is already merged in App.axaml).
            _ = Program.Services.GetRequiredService<Theming.IThemeService>().InitializeAsync();

            // Global safety net: surface unhandled UI-thread errors as a message box instead of crashing.
            InstallGlobalErrorHandler();

            desktop.MainWindow = mainWindow;
            // Pop-outs (DPS overlays / floating modules / info cards) are shown ownerless so they survive a main-window
            // minimize. Tie app lifetime to the main window so closing it exits even if an ownerless pop-out lingers
            // (the main window's close handler also tears them down explicitly, after the exit confirmation).
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InstallGlobalErrorHandler()
    {
        var dialogs = Program.Services.GetRequiredService<IDialogService>();
        var logger = Program.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true; // keep the app alive
            logger.LogError(e.Exception, "Unhandled UI exception");
            _ = dialogs.ShowMessageAsync(
                "Something went wrong",
                $"{e.Exception.Message}\n\n(The error was logged. You can keep using the app.)");
        };
    }
}
