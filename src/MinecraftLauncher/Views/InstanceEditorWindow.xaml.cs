using System.Windows;
using System.Windows.Input;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class InstanceEditorWindow : Window
{
    public InstanceEditorWindow()
    {
        InitializeComponent();
    }

    private InstanceEditorViewModel? ViewModel => DataContext as InstanceEditorViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.LoadVersionsAsync();
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null) return;
        ViewModel.SearchCommand.Execute(null);
        e.Handled = true;
    }
}