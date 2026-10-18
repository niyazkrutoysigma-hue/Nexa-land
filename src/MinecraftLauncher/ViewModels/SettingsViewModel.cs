using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Настройки: память, Java, пути, оформление, ключи API, обновления.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SettingsService _settings = SettingsService.Default;
    private int _ram;
    private string _javaPath = "";
    private string _accent = "#4C8DFF";
    private AppTheme _theme = AppTheme.Dark;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;

        SaveCommand = new RelayCommand(Save);
        PickInstancesFolderCommand = new RelayCommand(PickInstancesFolder);
        PickJavaCommand = new RelayCommand(PickJava);
        AutoDetectJavaCommand = new RelayCommand(RefreshJava);
        InstallJavaCommand = new AsyncRelayCommand(InstallJavaAsync);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.Root));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsDir));
        ClearCacheCommand = new RelayCommand(ClearCache);
        ApplyAccentCommand = new RelayCommand(ApplyAccent);
        RefreshCommand = new RelayCommand(() =>
        {
            Ram = _settings.DefaultRamMb;
            Accent = _settings.AccentColor;
            Theme = _settings.Theme;
            JavaPath = _settings.JavaPath;
            RefreshJava();
        });

        Accents =
        [
            ("#4C8DFF", "Синий"), ("#7C5CFF", "Фиолетовый"), ("#E85D9E", "Розовый"),
            ("#FF6B6B", "Красный"), ("#FF9F43", "Оранжевый"), ("#2ECC71", "Зелёный"),
            ("#1ABC9C", "Бирюзовый"), ("#F1C40F", "Жёлтый"), ("#95A5A6", "Серый")
        ];

        Ram = _settings.DefaultRamMb;
        Accent = _settings.AccentColor;
        Theme = _settings.Theme;
        JavaPath = _settings.JavaPath;
        RefreshJava();
    }

    public ObservableCollection<JavaLocator.JavaInfo> JavaList { get; } = [];
    public ObservableCollection<(string Hex, string Name)> Accents { get; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand PickInstancesFolderCommand { get; }
    public RelayCommand PickJavaCommand { get; }
    public RelayCommand AutoDetectJavaCommand { get; }
    public AsyncRelayCommand InstallJavaCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand ClearCacheCommand { get; }
    public RelayCommand ApplyAccentCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public int MaxRam => Math.Max(2048, SettingsService.Default.TotalRamMb - 1024);

    public string RamText => Ram >= 1024
        ? $"{Ram / 1024.0:0.#} ГБ из {MaxRam / 1024.0:0.#} ГБ"
        : $"{Ram} МБ";

    public string TotalRamText => $"{SettingsService.Default.TotalRamMb / 1024.0:0.#} ГБ оперативной памяти";

    public int Ram
    {
        get => _ram;
        set
        {
            var clamped = Math.Clamp(value, SettingsService.Default.MinRamMb, MaxRam);
            if (SetProperty(ref _ram, clamped))
            {
                OnPropertyChanged(nameof(RamText));
                _settings.DefaultRamMb = clamped;
                _settings.Save();
            }
        }
    }

    public string Accent
    {
        get => _accent;
        set => SetProperty(ref _accent, value);
    }

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                _settings.Theme = value;
                _settings.Save();
                ThemeManager.Default.Apply(value, _settings.AccentColor);
            }
        }
    }

    public string JavaPath
    {
        get => _javaPath;
        set
        {
            if (SetProperty(ref _javaPath, value))
            {
                _settings.JavaPath = value;
                _settings.Save();
            }
        }
    }

    public bool AutoInstallJava
    {
        get => _settings.AutoInstallJava;
        set
        {
            if (_settings.AutoInstallJava != value)
            {
                _settings.AutoInstallJava = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool CloseAfterLaunch
    {
        get => _settings.CloseAfterLaunch;
        set
        {
            if (_settings.CloseAfterLaunch != value)
            {
                _settings.CloseAfterLaunch = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool AutoUpdateLauncher
    {
        get => _settings.AutoUpdateLauncher;
        set
        {
            if (_settings.AutoUpdateLauncher != value)
            {
                _settings.AutoUpdateLauncher = value;
                _settings.Save();
                OnPropertyChanged();
            }
        }
    }

    public string InstancesFolder
    {
        get => SettingsService.Default.InstancesFolder;
        set
        {
            if (SettingsService.Default.InstancesFolder != value)
            {
                SettingsService.Default.InstancesFolder = value;
                SettingsService.Default.Save();
                OnPropertyChanged();
            }
        }
    }

    public string InstancesFolderInfo => SettingsService.Default.EffectiveInstancesFolder;

    public string CurseForgeApiKey
    {
        get => SettingsService.Default.CurseForgeApiKey;
        set
        {
            if (SettingsService.Default.CurseForgeApiKey != value)
            {
                SettingsService.Default.CurseForgeApiKey = value;
                SettingsService.Default.Save();
                OnPropertyChanged();
            }
        }
    }

    public string SelfUpdateRepo
    {
        get => SettingsService.Default.SelfUpdateRepo;
        set
        {
            if (SettingsService.Default.SelfUpdateRepo != value)
            {
                SettingsService.Default.SelfUpdateRepo = value;
                SettingsService.Default.Save();
                OnPropertyChanged();
            }
        }
    }

    public string LauncherBrand
    {
        get => SettingsService.Default.LauncherBrand;
        set
        {
            if (SettingsService.Default.LauncherBrand != value)
            {
                SettingsService.Default.LauncherBrand = value;
                SettingsService.Default.Save();
                OnPropertyChanged();
            }
        }
    }

    public string DataFolder => AppPaths.Root;

    private void Save()
    {
        _settings.Save();
        ThemeManager.Default.Apply(_settings.Theme, _settings.AccentColor);
        _main.ReportText("Настройки сохранены");
    }

    private void ApplyAccent()
    {
        _settings.AccentColor = Accent;
        _settings.Save();
        ThemeManager.Default.Apply(_settings.Theme, Accent);
        _main.ReportText("Цвет интерфейса обновлён");
    }

    private void RefreshJava()
    {
        AppServices.Java.Refresh();
        JavaList.Clear();
        foreach (var java in AppServices.Java.Found) JavaList.Add(java);
        OnPropertyChanged(nameof(JavaSummary));
    }

    public string JavaSummary => JavaList.Count == 0
        ? "Java не найдена — будет скачана автоматически"
        : $"Найдено версий: {JavaList.Count}";

    private async Task InstallJavaAsync()
    {
        await _main.RunAsync("Скачиваем Java 21…", async () =>
        {
            var progress = new Progress<InstallProgress>(_main.ReportProgress);
            var java = await AppServices.Adoptium.DownloadJavaAsync(21, progress);
            if (java is not null)
            {
                JavaPath = java.Directory;
                RefreshJava();
            }
        });
    }

    private void PickInstancesFolder()
    {
        var folder = AppServices.Dialogs.PickFolder(SettingsService.Default.EffectiveInstancesFolder);
        if (folder is null) return;
        InstancesFolder = folder;
        OnPropertyChanged(nameof(InstancesFolderInfo));
        LogService.Default.Info($"Папка инстансов: {folder}");
    }

    private void PickJava()
    {
        var folder = AppServices.Dialogs.PickFolder();
        if (folder is null) return;
        JavaPath = folder;
        RefreshJava();
    }

    private void ClearCache()
    {
        try
        {
            var size = Directory.Exists(AppPaths.CacheDir)
                ? new DirectoryInfo(AppPaths.CacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;

            if (Directory.Exists(AppPaths.CacheDir)) Directory.Delete(AppPaths.CacheDir, true);
            AppPaths.Ensure(AppPaths.CacheDir);
            AppPaths.Ensure(AppPaths.TempDir);
            AppPaths.Ensure(AppPaths.DownloadsDir);

            _main.ReportText($"Освобождено {DownloadStatus.Format(size)}");
            LogService.Default.Success($"Кэш очищен ({DownloadStatus.Format(size)})");
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Не удалось очистить кэш", ex);
        }
    }

    private static void OpenFolder(string path)
    {
        AppPaths.Ensure(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}

/// <summary>Логи лаунчера и игры.</summary>
public sealed class ConsoleViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _autoScroll = true;
    private string _filter = "";

    public ConsoleViewModel(MainViewModel main)
    {
        _main = main;
        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(LogService.Default.Entries);
        _view.Filter = o => Matches(o as LogEntry);

        ClearCommand = new RelayCommand(() => LogService.Default.Entries.ClearSafe());
        CopyCommand = new RelayCommand(CopyAll);
        OpenLogsCommand = new RelayCommand(() =>
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true }));
    }

    private readonly System.ComponentModel.ICollectionView _view;

    public System.Collections.IEnumerable Entries => _view;

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value)) _view.Refresh();
        }
    }

    private bool Matches(LogEntry? entry)
    {
        if (entry is null) return false;
        if (string.IsNullOrWhiteSpace(_filter)) return true;

        return entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || entry.Level.ToString().Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private void CopyAll()
    {
        try
        {
            var text = string.Join(Environment.NewLine,
                LogService.Default.Entries.Select(e => $"[{e.TimeText}] [{e.Level}] {e.Message}"));
            System.Windows.Clipboard.SetText(text);
            _main.ReportText("Лог скопирован в буфер обмена");
        }
        catch (Exception ex)
        {
            LogService.Default.Warn("Буфер обмена недоступен: " + ex.Message);
        }
    }
}