using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class StoreView : UserControl
{
    private bool _loaded;

    public StoreView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        if (DataContext is StoreViewModel vm) await vm.LoadAsync();
    }
}