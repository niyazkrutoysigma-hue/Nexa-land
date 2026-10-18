using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.Models;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.Tag is not InstanceProfile instance) return;

        await ViewModel.PlayAsync(instance);
    }

    private void OnModsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.Tag is not InstanceProfile instance) return;

        ViewModel?.OpenMods(instance);
    }
}
