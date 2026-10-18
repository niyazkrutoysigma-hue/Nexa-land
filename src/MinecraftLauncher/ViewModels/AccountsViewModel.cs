using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Аккаунты: вход по коду Microsoft и оффлайн-профили.</summary>
public sealed class AccountsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private Account? _selected;
    private string _status = "Для сетевой игры нужен аккаунт Microsoft с Minecraft: Java Edition";
    private string _deviceCode = string.Empty;
    private string _deviceUrl = string.Empty;

    public AccountsViewModel(MainViewModel main)
    {
        _main = main;

        AddMicrosoftCommand = new AsyncRelayCommand(AddMicrosoftAsync);
        AddOfflineCommand = new RelayCommand(AddOffline);
        RemoveCommand = new RelayCommand(RemoveSelected, () => Selected is not null);
        SetActiveCommand = new RelayCommand(SetActive, () => Selected is not null);
        RefreshTokenCommand = new AsyncRelayCommand(RefreshTokenAsync, () => Selected is { IsMicrosoft: true });
        CopyCodeCommand = new RelayCommand(CopyCode, () => DeviceCode.Length > 0);
        OpenBrowserCommand = new RelayCommand(() => OpenBrowser(DeviceUrl), () => DeviceUrl.Length > 0);
        RefreshCommand = new RelayCommand(() => Refresh());

        Refresh();
    }

    public ObservableCollection<Account> Accounts { get; } = [];

    public AsyncRelayCommand AddMicrosoftCommand { get; }
    public RelayCommand AddOfflineCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SetActiveCommand { get; }
    public AsyncRelayCommand RefreshTokenCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand OpenBrowserCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public Account? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) UpdateCommands();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string DeviceCode
    {
        get => _deviceCode;
        private set
        {
            if (SetProperty(ref _deviceCode, value)) CopyCodeCommand.RaiseCanExecuteChanged();
        }
    }

    public string DeviceUrl
    {
        get => _deviceUrl;
        private set
        {
            if (SetProperty(ref _deviceUrl, value)) OpenBrowserCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasDeviceCode => DeviceCode.Length > 0;

    public bool HasAccounts => Accounts.Count > 0;

    public string EmptyText => "Аккаунтов нет. Добавьте Microsoft-аккаунт или оффлайн-профиль.";

    private void UpdateCommands()
    {
        RemoveCommand.RaiseCanExecuteChanged();
        SetActiveCommand.RaiseCanExecuteChanged();
        RefreshTokenCommand.RaiseCanExecuteChanged();
    }

    private void Refresh()
    {
        Accounts.Clear();
        foreach (var account in AppServices.Accounts.Accounts) Accounts.Add(account);

        Selected = AppServices.Accounts.Active ?? Accounts.FirstOrDefault();
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(EmptyText));
        UpdateCommands();
        _main.RefreshActiveAccount();
    }

    private async Task AddMicrosoftAsync()
    {
        DeviceCode = string.Empty;
        DeviceUrl = string.Empty;
        OnPropertyChanged(nameof(HasDeviceCode));

        var clientId = SettingsService.Default.MsaClientId;

        await _main.RunAsync("Запрашиваем код авторизации…", async () =>
        {
            try
            {
                var code = await AppServices.Auth.StartDeviceCodeAsync(clientId);
                DeviceCode = code.UserCode;
                DeviceUrl = code.VerificationUri;
                OnPropertyChanged(nameof(HasDeviceCode));
                Status = $"Код: {code.UserCode}. Откройте {code.VerificationUri} и введите код.";

                OpenBrowser(code.VerificationUriComplete ?? code.VerificationUri);

                var progress = new Progress<string>(text => Status = text);
                var token = await AppServices.Auth.WaitForTokenAsync(clientId, code.DeviceCode, code.Interval,
                    progress, null);

                if (token is null)
                {
                    Status = "Время ожидания истекло. Попробуйте снова.";
                    return;
                }

                var account = await AppServices.Auth.CompleteLoginAsync(token);
                if (account is null)
                {
                    Status = "Не удалось получить профиль Minecraft";
                    return;
                }

                AppServices.Accounts.Add(account);
                await AppServices.Icons.DownloadAvatarAsync(account);
                AppServices.Accounts.Active = account;

                Status = $"Готово: {account.DisplayName}";
                DeviceCode = string.Empty;
                OnPropertyChanged(nameof(HasDeviceCode));
                Refresh();
            }
            catch (UnauthorizedAccessException ex)
            {
                Status = ex.Message;
                LogService.Default.Warn(ex.Message);
            }
        });

        Refresh();
    }

    private void AddOffline()
    {
        var name = AppServices.Dialogs.PromptText("Оффлайн-профиль",
            "Имя игрока. Работает для одиночной игры и серверов в режиме offline.");

        if (string.IsNullOrWhiteSpace(name)) return;

        var account = new Account
        {
            Type = "Offline",
            DisplayName = name.Trim(),
            Uuid = LaunchService.OfflineUuid(name.Trim())
        };

        AppServices.Accounts.Add(account);
        AppServices.Accounts.Active = account;
        Refresh();
        Status = $"Оффлайн-профиль «{account.DisplayName}» создан";
    }

    private void RemoveSelected()
    {
        if (Selected is null) return;
        if (!AppServices.Dialogs.Confirm("Удалить аккаунт", $"Удалить «{Selected.DisplayName}» из лаунчера?"))
            return;

        AppServices.Accounts.Remove(Selected);
        Selected = null;
        Refresh();
    }

    private void SetActive()
    {
        if (Selected is null) return;
        AppServices.Accounts.Active = Selected;
        Refresh();
        Status = $"Активный аккаунт: {Selected.DisplayName}";
    }

    private async Task RefreshTokenAsync()
    {
        if (Selected is not { IsMicrosoft: true } account) return;

        await _main.RunAsync("Обновляем токен…", async () =>
        {
            try
            {
                var clientId = account.MsaClientId ?? SettingsService.Default.MsaClientId;
                var refreshed = await AppServices.Auth.RefreshAsync(clientId, account.RefreshToken ?? "");
                AppServices.Accounts.UpdateTokens(account, refreshed.AccessToken ?? "",
                    refreshed.RefreshToken, refreshed.Xuid);
                Status = "Токен обновлён";
            }
            catch (Exception ex)
            {
                AppServices.Accounts.SetError(account, ex.Message);
                Status = "Не удалось обновить: " + ex.Message;
                throw;
            }
        });

        Refresh();
    }

    private void CopyCode()
    {
        if (string.IsNullOrEmpty(DeviceCode)) return;
        try
        {
            System.Windows.Clipboard.SetText(DeviceCode);
            Status = "Код скопирован в буфер обмена";
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Буфер обмена недоступен: {ex.Message}");
        }
    }

    private void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось открыть браузер: {ex.Message}");
        }
    }
}