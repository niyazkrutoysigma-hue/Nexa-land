using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

public interface IPage
{
    string Key { get; }
    string Title { get; }
    string Subtitle { get; }
    string Icon { get; }
}

public sealed class MainViewModel : ObservableObject
{
    /// <summary>Текущая модель — нужна code-behind'у представлений.</summary>
    public static MainViewModel? Current { get; private set; }

    private PageViewModel? _selectedPage;
    private string _statusText = "Готов";
    private double _progress;
    private bool _isBusy;
    private string? _activeAccountText;
    private string _updateInfo = string.Empty;
    private ObservableCollection<Toast> _toasts = [];

    public MainViewModel()
    {
        Current = this;

        Pages =
        [
            new PageViewModel("home", "Главная", "Быстрый запуск и обзор",
                "M12,3 L21,12 L12,21 L3,12 Z M12,8 L16,12 L12,16 L8,12 Z", new HomeViewModel(this)),
            new PageViewModel("instances", "Сборки", "Установленные версии и сборки",
                "M4,5 L12,3 L20,5 L20,15 L12,17 L4,15 Z M4,5 L12,7 L20,5 M12,7 L12,17",
                new InstancesViewModel(this)),
            new PageViewModel("store", "Каталог версий", "Релизы и снапшоты Minecraft",
                "M12,3 L12,13 M8,10 L12,14 L16,10 M4,17 L20,17", new StoreViewModel(this)),
            new PageViewModel("accounts", "Аккаунты", "Microsoft и оффлайн-профили",
                "M12,5 m -4,0 a 4,4 0 1,0 8,0 a 4,4 0 1,0 -8,0 M4,20 c 0,-4.5 3.6,-6.5 8,-6.5 s 8,2 8,6.5 z",
                new AccountsViewModel(this)),
            new PageViewModel("settings", "Настройки", "Java, память, оформление",
                "M4,7 L20,7 M4,12 L20,12 M4,17 L20,17 M9,5 L9,9 M15,10 L15,14 M7,15 L7,19",
                new SettingsViewModel(this)),
            new PageViewModel("console", "Консоль", "Логи лаунчера и игры",
                "M4,5 L20,5 L20,17 L4,17 Z M7,9 L10,11 L7,13 M12,13 L17,13",
                new ConsoleViewModel(this)),
        ];

        SelectedPage = Pages[0];

        AppServices.Launch.InstanceStarted += _ => OnPropertyChanged(nameof(StatusText));
        AppServices.Launch.InstanceStopped += _ => OnPropertyChanged(nameof(StatusText));

        AppServices.Accounts.Active?.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Models.Account.DisplayName)) RefreshActiveAccount();
        };

        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        RefreshAllCommand = new RelayCommand(() =>
        {
            RefreshPages();
            PushToast(new Toast { Title = "Списки обновлены" });
        });
    }

    public ObservableCollection<PageViewModel> Pages { get; }

    public string Version => "v" + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);

    public string BrandTitle => NexaBrand.Title;

    public string BrandTagline => NexaBrand.Tagline;

    public string UpdateInfo
    {
        get => _updateInfo;
        set => SetProperty(ref _updateInfo, value);
    }

    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set => SetProperty(ref _selectedPage, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string? ActiveAccountText
    {
        get => _activeAccountText;
        set => SetProperty(ref _activeAccountText, value);
    }

    public ObservableCollection<Toast> Toasts => _toasts;








    public AsyncRelayCommand CheckUpdatesCommand { get; }

    public RelayCommand RefreshAllCommand { get; }

    public void RefreshActiveAccount()
    {
        var account = AppServices.Accounts.Active;
        ActiveAccountText = account is null ? "Аккаунт не выбран" : $"{account.DisplayName} · {account.TypeLabel}";
    }

    public void RefreshOrbs()
    {
    }

    /// <summary>Показать всплывающее уведомление в углу окна.</summary>
    public void PushToast(Toast toast)
    {
        Toasts.Add(toast);

        // убираем через 6 секунд
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };

        timer.Start();
    }

    public void DismissToast(Toast toast) => Toasts.Remove(toast);

    /// <summary>Обёртка для длительных операций с индикацией в статус-баре.</summary>
    public async Task RunAsync(string status, Func<Task> action)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = status;
        Progress = 0;

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Отменено";
        }
        catch (Exception ex)
        {
            LogService.Default.Error(status + " — ошибка", ex);
            StatusText = "Ошибка: " + ex.Message;
            PushToast(new Toast { Title = "Ошибка", Message = ex.Message, Level = LogLevel.Error });
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    public void ReportProgress(InstallProgress progress)
    {
        StatusText = progress.Text;
        Progress = Math.Clamp(progress.Percent / 100.0, 0, 1);
    }

    public void ReportText(string text) => StatusText = text;

    /// <summary>Обновить страницы, которые показывают состояние сборок.</summary>
    public void RefreshPages()
    {
        foreach (var page in Pages)
        {
            if (page.Content is HomeViewModel home) home.Refresh();
            if (page.Content is InstancesViewModel instances) instances.Refresh();
        }
    }

    private async Task CheckUpdatesAsync()
    {
        await RunAsync("Проверяем обновления лаунчера…", async () =>
        {
            var update = await AppServices.Updates.CheckAsync();
            UpdateInfo = update switch
            {
                null => "Автообновление не настроено (укажите репозиторий в настройках)",
                { IsNewer: true } => $"Доступна версия {update.Version} ({DownloadStatus.Format(update.Size)})",
                _ => "Установлена последняя версия"
            };

            StatusText = UpdateInfo;

            if (update is { IsNewer: true })
            {
                PushToast(new Toast
                {
                    Title = $"Доступно обновление {update.Version}",
                    Message = "Нажмите, чтобы открыть страницу релиза",
                    Level = LogLevel.Success,
                    WithAction = true,
                    ActionText = "Открыть",
                    Action = () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://github.com/{SettingsService.Default.SelfUpdateRepo}/releases/latest")
                    { UseShellExecute = true })
                });
            }
        });
    }
}

public sealed class PageViewModel : IPage
{
    public PageViewModel(string key, string title, string subtitle, string icon, object content)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        Content = content;
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Icon { get; }
    public object Content { get; }

    /// <summary>Заголовок страницы для заголовка окна.</summary>
    public string Header => $"{Title} — {Subtitle}";
}