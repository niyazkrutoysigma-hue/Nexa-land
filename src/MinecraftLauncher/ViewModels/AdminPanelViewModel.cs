using System.Windows;
using MinecraftLauncher.Common;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Админ-панель: доступ по паролю.</summary>
public sealed class AdminPanelViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isAuthenticated;
    private string _password = "";
    private string _status = "Введите пароль администратора";

    public AdminPanelViewModel(MainViewModel main)
    {
        _main = main;
        AuthenticateCommand = new RelayCommand(Authenticate);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public RelayCommand AuthenticateCommand { get; }

    private void Authenticate()
    {
        if (Password == "Niyaz201424")
        {
            IsAuthenticated = true;
            Status = "Добро пожаловать, администратор!";
            ToastService.Default.Success("Админ-панель", "Доступ разрешён");
        }
        else
        {
            Status = "Неверный пароль";
            ToastService.Default.Error("Ошибка", "Неверный пароль");
        }
    }
}