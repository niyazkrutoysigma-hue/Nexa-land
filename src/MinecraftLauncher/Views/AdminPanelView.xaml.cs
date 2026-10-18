using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class AdminPanelView : UserControl
{
    public AdminPanelView()
    {
        InitializeComponent();
    }

    private AdminPanelViewModel? ViewModel => DataContext as AdminPanelViewModel;

    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.Password = PasswordBox.Password;
        ViewModel.AuthenticateCommand.Execute(null);
    }

    private void OnOpenBotClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://t.me/nexa_land_bot") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось открыть браузер: {ex.Message}");
        }
    }
}