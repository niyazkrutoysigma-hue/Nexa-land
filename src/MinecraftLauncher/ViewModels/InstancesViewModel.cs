using System.Collections.ObjectModel;
using System.Windows;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

public sealed class InstancesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private InstanceProfile? _selected;
    private string _search = string.Empty;

    public InstancesViewModel(MainViewModel main)
    {
        _main = main;

        NewCommand = new RelayCommand(CreateInstance);
        EditCommand = new RelayCommand(EditInstance, () => Selected is not null);
        DeleteCommand = new RelayCommand(DeleteInstance, () => Selected is not null);
        LaunchCommand = new AsyncRelayCommand(LaunchSelectedAsync, () => Selected is not null);
        OpenFolderCommand = new RelayCommand(() => OpenFolder(Selected), () => Selected is not null);
        OpenModsCommand = new RelayCommand(() => OpenModsWindow(Selected), () => Selected is not null);
        RevealVersionCommand = new RelayCommand(ShowVersionsFolder, () => Selected is not null);
        DuplicateCommand = new RelayCommand(DuplicateInstance, () => Selected is not null);
        RegenerateIconCommand = new RelayCommand(RegenerateIcon, () => Selected is not null);
        RefreshCommand = new RelayCommand(Refresh);

        Refresh();
    }

    public ObservableCollection<InstanceProfile> Instances { get; } = [];

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Refresh();
        }
    }

    public InstanceProfile? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value)) UpdateCommands();
        }
    }

    public string EmptyText => Instances.Count == 0
        ? "Пока нет ни одного инстанса. Нажмите «Создать» или перейдите в «Каталог версий»."
        : "Ничего не найдено по фильтру «" + Search + "»";

    public bool HasInstances => Instances.Count > 0;

    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenModsCommand { get; }

    /// <summary>Меню дополнений сборки (моды): то, что раньше пряталось под «три точки».</summary>
    private static Views.ModsWindow? _modsWindow;

    public static void OpenModsWindow(InstanceProfile? instance)
    {
        if (instance is null) return;

        // одно окно на всё приложение: повторное нажатие просто поднимает его
        if (_modsWindow is { IsLoaded: true })
        {
            if (_modsWindow.DataContext is ModsManagerViewModel existing) existing.LoadInstalled();
            _modsWindow.Activate();
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;

        Views.ModsWindow window = null!;
        window = new Views.ModsWindow
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? System.Windows.WindowStartupLocation.CenterScreen
                : System.Windows.WindowStartupLocation.CenterOwner
        };

        window.DataContext = new ModsManagerViewModel(instance, MainViewModel.Current!, () =>
        {
            _modsWindow = null;
            window.Close();
        });

        _modsWindow = window;
        window.Closed += (_, _) => _modsWindow = null;
        window.Show();
        window.Activate();
    }
    public RelayCommand RevealVersionCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand RegenerateIconCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void Refresh()
    {
        Instances.Clear();
        foreach (var instance in AppServices.Instances.Instances.Where(Matches))
        {
            Instances.Add(instance);
        }

        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(EmptyText));
        if (Selected is null && Instances.Count > 0) Selected = Instances[0];
    }

    private bool Matches(InstanceProfile instance)
    {
        if (instance.Hidden) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;

        return instance.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || instance.MinecraftVersion.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || instance.LoaderName.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCommands()
    {
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        LaunchCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        OpenModsCommand.RaiseCanExecuteChanged();
        RevealVersionCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        RegenerateIconCommand.RaiseCanExecuteChanged();
    }

    private void CreateInstance()
    {
        var draft = AppServices.Dialogs.ShowInstanceEditor(null);
        if (draft is null) return;

        AppServices.Instances.Add(draft);
        AppServices.Icons.GenerateInstanceIcon(draft);
        Refresh();
        Selected = draft;
        _main.ReportText($"Создан инстанс «{draft.Name}». Файлы скачаются при первом запуске.");
    }

    private void EditInstance()
    {
        if (Selected is null) return;

        var edited = AppServices.Dialogs.ShowInstanceEditor(Selected);
        if (edited is null) return;

        AppServices.Instances.Save();
        AppServices.Icons.GenerateInstanceIcon(edited);
        Refresh();
        _main.ReportText($"Инстанс «{edited.Name}» обновлён");
    }

    private void DeleteInstance()
    {
        if (Selected is null) return;
        if (!AppServices.Dialogs.Confirm("Удалить инстанс",
                $"Инстанс «{Selected.Name}» и его папка ({Selected.FolderInfo}) будут удалены без возможности восстановления."))
        {
            return;
        }

        var name = Selected.Name;
        AppServices.Instances.Remove(Selected);
        Selected = null;
        Refresh();
        _main.ReportText($"Инстанс «{name}» удалён");
    }

    private void DuplicateInstance()
    {
        if (Selected is null) return;

        var copy = new InstanceProfile
        {
            Name = Selected.Name + " (копия)",
            Group = Selected.Group,
            MinecraftVersion = Selected.MinecraftVersion,
            VersionUrl = Selected.VersionUrl,
            VersionJsonId = Selected.VersionJsonId,
            VersionType = Selected.VersionType,
            ReleaseTime = Selected.ReleaseTime,
            Loader = Selected.Loader,
            LoaderVersion = Selected.LoaderVersion,
            JavaPath = Selected.JavaPath,
            RamMb = Selected.RamMb,
            JavaArgs = Selected.JavaArgs,
            MinecraftArgs = Selected.MinecraftArgs,
            Isolated = Selected.Isolated
        };

        AppServices.Instances.Add(copy);
        AppServices.Icons.GenerateInstanceIcon(copy);
        Refresh();
        Selected = copy;
    }

    private void RegenerateIcon()
    {
        if (Selected is null) return;
        AppServices.Icons.GenerateInstanceIcon(Selected);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Selected));
    }

    private async Task LaunchSelectedAsync()
    {
        if (Selected is null) return;
        await LaunchInstanceAsync(Selected, _main);
    }

    /// <summary>Общий сценарий запуска: скачать недостающее, поднять Java и стартовать процесс.</summary>
    public static async Task LaunchInstanceAsync(InstanceProfile instance, MainViewModel main)
    {
        if (AppServices.Launch.IsRunning(instance.Id))
        {
            main.ReportText($"«{instance.Name}» уже запущен");
            return;
        }

        var account = AppServices.Accounts.Active;
        if (account is null)
        {
            main.ReportText("Сначала добавьте аккаунт на вкладке «Аккаунты»");
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.VersionUrl))
        {
            // ссылка на version.json могла потеряться (сборка без установки) — восстанавливаем из манифеста
            if (!await AppServices.Setup.TryResolveVersionUrlAsync(instance))
            {
                main.ReportText($"У сборки «{instance.Name}» не выбрана версия Minecraft — откройте «Изменить»");
                return;
            }
        }

        var progress = new Progress<InstallProgress>(main.ReportProgress);
        using var cts = new CancellationTokenSource();

        await main.RunAsync($"Подготовка «{instance.Name}»…", async () =>
        {
            var version = await AppServices.Setup.EnsureReadyAsync(instance, progress, cts.Token);

            // Обновляем токен аккаунта, если он протух
            if (account.IsMicrosoft && !string.IsNullOrEmpty(account.RefreshToken))
            {
                try
                {
                    var refreshed = await AppServices.Auth.RefreshAsync(
                        account.MsaClientId ?? SettingsService.Default.MsaClientId,
                        account.RefreshToken, cts.Token);
                    AppServices.Accounts.UpdateTokens(account, refreshed.AccessToken ?? "",
                        refreshed.RefreshToken, refreshed.Xuid);
                }
                catch (Exception ex)
                {
                    LogService.Default.Warn($"Токен не обновился: {ex.Message}");
                }
            }

            var config = AppServices.Launch.Prepare(instance, version, account);
            LogService.Default.Info($"Класс-пас: {config.Classpath.Count} элементов, главный класс {config.MainClass}");

            // Страховка от падения JVM: если часть библиотек не скачалась, игра упадёт
            // с NoClassDefFoundError. Лучше сказать об этом прямо.
            var missing = AppServices.Launch.MissingLibraries(version, instance);
            if (missing.Count > 0)
            {
                var list = string.Join(", ", missing.Take(3).Select(Path.GetFileName));
                LogService.Default.Error($"Не скачано библиотек: {missing.Count} ({list})");

                main.ReportText($"Не хватает {missing.Count} библиотек — повторная загрузка…");
                await AppServices.Installer.InstallAsync(version, InstallTarget.For(instance), progress,
                    includeClient: true, cts.Token);
                AppServices.Instances.Save();

                var stillMissing = AppServices.Launch.MissingLibraries(version, instance);
                if (stillMissing.Count > 0)
                    throw new IOException(
                        $"Не удалось скачать библиотеки: {string.Join(", ", stillMissing.Take(3).Select(Path.GetFileName))}. " +
                        "Проверьте интернет и запустите установку версии заново.");
            }

            main.ReportText($"Запуск «{instance.Name}»…");
            _ = Task.Run(() => AppServices.Launch.LaunchAsync(config, cts.Token));

            if (SettingsService.Default.CloseAfterLaunch)
                Application.Current?.Shutdown();
        });

        AppServices.Instances.Save();
    }

    private static void OpenFolder(InstanceProfile? instance, bool mods = false)
    {
        if (instance is null) return;

        if (mods)
        {
            OpenModsWindow(instance);
            return;
        }

        var path = instance.GameDir;
        AppServices.Dialogs.RevealInExplorer(path);
    }

    private void ShowVersionsFolder()
    {
        if (Selected is null) return;
        var path = Selected.VersionsRoot;
        AppPaths.Ensure(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}