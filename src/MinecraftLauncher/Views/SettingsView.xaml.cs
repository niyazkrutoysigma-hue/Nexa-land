using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if ((sender as FrameworkElement)?.Tag is not string hex) return;

        vm.Accent = hex;
        vm.ApplyAccentCommand.Execute(null);
    }
}