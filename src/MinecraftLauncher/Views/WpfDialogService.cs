using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;
using MinecraftLauncher.ViewModels;
using Microsoft.Win32;

namespace MinecraftLauncher.Views;

/// <summary>Реализация диалогов поверх стандартных WPF-окон.</summary>
public sealed class WpfDialogService : IDialogService
{
    public InstanceProfile? ShowInstanceEditor(InstanceProfile? existing)
    {
        var vm = new InstanceEditorViewModel(existing);
        var window = new InstanceEditorWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        window.ShowDialog();
        return vm.Result;
    }

    public bool Confirm(string title, string message)
    {
        var result = MessageBox.Show(Application.Current.MainWindow, message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public string PromptText(string title, string message, string initial = "")
    {
        var dialog = new TextInputDialog(title, message, initial)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.Value : string.Empty;
    }

    public string? PickFolder(string? initial = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку",
            InitialDirectory = initial ?? AppPaths.Root,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string[]? PickFiles(params (string Title, string Pattern)[] filters)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файлы",
            Multiselect = true,
            CheckFileExists = true
        };

        foreach (var (title, pattern) in filters)
        {
            dialog.Filter = string.IsNullOrEmpty(dialog.Filter)
                ? $"{title}|{pattern}"
                : $"{dialog.Filter}|{title}|{pattern}";
        }

        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось открыть в проводнике: {ex.Message}");
        }
    }
}

/// <summary>Простое окно ввода строки.</summary>
public partial class TextInputDialog : Window
{
    public TextInputDialog(string title, string message, string initial)
    {
        Title = title;
        InitializeComponent();

        MessageText.Text = message;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public string Value => InputBox.Text;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnOk(sender, e);
    }
}