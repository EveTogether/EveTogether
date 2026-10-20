using System;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The loot table (ET-519), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating, so it can stay open beside the run window.</summary>
public partial class ActivityLootWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public ActivityLootWindow() => AvaloniaXamlLoader.Load(this);

    public ActivityLootWindow(LootTableViewModel viewModel) : this() => DataContext = viewModel;
}
