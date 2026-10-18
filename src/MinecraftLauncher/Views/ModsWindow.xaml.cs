using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class ModsWindow : Window
{
    public ModsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FitToWorkArea();
    }

    /// <summary>Окно дополнений тоже подгоняем под рабочую область монитора.</summary>
    private void FitToWorkArea()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            if (Height > area.Height - 40) Height = area.Height - 40;
            if (Width > area.Width - 40) Width = area.Width - 40;
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Размер окна модов не скорректирован: {ex.Message}");
        }
    }

    private ModsManagerViewModel? ViewModel => DataContext as ModsManagerViewModel;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is null) return;

        ViewModel.Search = ViewModel.Search.Trim();
        _ = ViewModel.LoadCatalogAsync(false);
        e.Handled = true;
    }

    private async void OnModActionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.Tag is not ViewModels.ModCard card) return;

        ViewModel.SelectedCard = card;

        if (card.Installed)
        {
            await ViewModel.UpdateCommand.ExecuteAsync();
        }
        else
        {
            await ViewModel.InstallCommand.ExecuteAsync();
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ViewModels.InstalledMod mod) return;
        ViewModel?.ToggleCommand.Execute(mod);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ViewModels.InstalledMod mod) return;
        ViewModel?.RemoveCommand.Execute(mod);
    }

    /// <summary>Автоподгрузка следующей страницы при прокрутке вниз.</summary>
    private void OnGetCurseForgeKeyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://console.curseforge.com/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось открыть браузер: {ex.Message}");
        }
    }

    private void OnCatalogScroll(object sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is null || e.VerticalChange == 0) return;

        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableHeight <= 0) return;
        if (viewer.VerticalOffset < viewer.ViewportHeight * 0.7) return;

        ViewModel.LoadMoreCommand.Execute(null);
    }
}