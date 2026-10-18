using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.Models;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class InstancesView : UserControl
{
    public InstancesView() => InitializeComponent();

    private InstancesViewModel? ViewModel => DataContext as InstancesViewModel;

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.Tag is not InstanceProfile instance) return;

        ViewModel.Selected = instance;
        await InstancesViewModel.LaunchInstanceAsync(instance, MainViewModel.Current!);
    }

    /// <summary>Плюсик — меню дополнений сборки (Modrinth / CurseForge / установленные моды).</summary>
    private void OnModsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.Tag is not InstanceProfile instance) return;

        ViewModel.Selected = instance;
        InstancesViewModel.OpenModsWindow(instance);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var button = (FrameworkElement)sender;
        if (button.Tag is not InstanceProfile instance) return;

        ViewModel.Selected = instance;

        var context = new ContextMenu();
        AddItem(context, "Играть", async () =>
            await InstancesViewModel.LaunchInstanceAsync(instance, MainViewModel.Current!));
        AddItem(context, "Дополнения (моды)", () => InstancesViewModel.OpenModsWindow(instance));
        AddItem(context, "Изменить настройки", () => ViewModel.EditCommand.Execute(null));
        AddItem(context, "Дублировать", () => ViewModel.DuplicateCommand.Execute(null));
        AddItem(context, "Экспорт модпака", async () => await AppServices.Modpacks.ExportAsync(instance));
        context.Items.Add(new Separator());
        AddItem(context, "Папка игры", () => AppServices.Dialogs.RevealInExplorer(instance.GameDir));
        AddItem(context, "Папка модов", () => AppServices.Dialogs.RevealInExplorer(instance.ModsDir));
        AddItem(context, "Папка миров", () => AppServices.Dialogs.RevealInExplorer(Path.Combine(instance.GameDir, "saves")));
        AddItem(context, "Папка конфигов", () => AppServices.Dialogs.RevealInExplorer(Path.Combine(instance.GameDir, "config")));
        AddItem(context, "Журнал игры", () => AppServices.Dialogs.RevealInExplorer(Path.Combine(instance.GameDir, "logs")));
        AddItem(context, "Папка версий", () => AppServices.Dialogs.RevealInExplorer(instance.VersionsRoot));
        AddItem(context, "Новая иконка", () => ViewModel.RegenerateIconCommand.Execute(null));
        context.Items.Add(new Separator());
        AddItem(context, "Удалить", () => ViewModel.DeleteCommand.Execute(null));

        context.PlacementTarget = button;
        context.IsOpen = true;
    }

    private static void AddItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}