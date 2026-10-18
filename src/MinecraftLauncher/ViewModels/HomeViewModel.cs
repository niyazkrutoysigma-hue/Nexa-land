using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Главная: быстрый запуск последней сборки, статистика и быстрые действия.</summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private InstanceProfile? _lastPlayed;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;

        PlayLastCommand = new AsyncRelayCommand(PlayLastAsync, () => LastPlayed is not null);
        CreateCommand = new RelayCommand(() => GoTo("instances"));
        StoreCommand = new RelayCommand(() => GoTo("store"));
        ModsCommand = new RelayCommand(OpenModsForLast, () => LastPlayed is not null);
        SettingsCommand = new RelayCommand(() => GoTo("settings"));
        RefreshCommand = new RelayCommand(Refresh);

        Refresh();
    }

    public ObservableCollection<InstanceProfile> Recent { get; } = [];

    public AsyncRelayCommand PlayLastCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand StoreCommand { get; }
    public RelayCommand ModsCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public InstanceProfile? LastPlayed
    {
        get => _lastPlayed;
        private set
        {
            if (SetProperty(ref _lastPlayed, value))
            {
                OnPropertyChanged(nameof(HasLastPlayed));
                OnPropertyChanged(nameof(LastPlayedTitle));
                OnPropertyChanged(nameof(LastPlayedMeta));
                PlayLastCommand.RaiseCanExecuteChanged();
                ModsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasLastPlayed => LastPlayed is not null;

    public string LastPlayedTitle => LastPlayed is null
        ? "Пока нет запущенных сборок"
        : LastPlayed.Name;

    public string LastPlayedMeta => LastPlayed is null
        ? "Создайте сборку или установите готовую версию"
        : $"{LastPlayed.DisplayVersion} · {LastPlayed.LoaderName}" +
          (LastPlayed.LastPlayed is { } date ? $" · играли {date:dd.MM}" : string.Empty);

    public int InstancesCount => AppServices.Instances.Instances.Count(i => !i.Hidden);

    public int ModsCount => Recent.Sum(i => i.EnabledModsCount);

    public string AccountText => AppServices.Accounts.Active is { } account
        ? $"{account.DisplayName} · {account.TypeLabel}"
        : "Аккаунт не выбран";

    public string AccountHint => AppServices.Accounts.Active is { IsMicrosoft: true }
        ? "Microsoft — доступны сетевые серверы"
        : "Оффлайн — одиночная игра и серверы без авторизации";

    public string JavaText
    {
        get
        {
            AppServices.Java.Refresh();
            var java = AppServices.Java.FindBest(21);
            return java is null ? "Java не найдена — будет скачана" : java.Name;
        }
    }

    public string SharedSizeText
    {
        get
        {
            try
            {
                var size = Directory.Exists(AppPaths.SharedStore)
                    ? new DirectoryInfo(AppPaths.SharedStore).EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length)
                    : 0;

                return $"Общий стор: {DownloadStatus.Format(size)}";
            }
            catch
            {
                return "Общий стор: —";
            }
        }
    }

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var greeting = hour switch
            {
                < 5 => "Доброй ночи",
                < 12 => "Доброе утро",
                < 18 => "Добрый день",
                _ => "Добрый вечер"
            };

            return $"{greeting}!";
        }
    }

    public void Refresh()
    {
        Recent.Clear();
        foreach (var instance in AppServices.Instances.Instances
                     .Where(i => !i.Hidden)
                     .OrderByDescending(i => i.LastPlayed ?? DateTime.MinValue)
                     .ThenBy(i => i.Name)
                     .Take(6))
        {
            Recent.Add(instance);
        }

        LastPlayed = Recent.FirstOrDefault(i => i.LastPlayed is not null) ?? Recent.FirstOrDefault();

        OnPropertyChanged(nameof(InstancesCount));
        OnPropertyChanged(nameof(ModsCount));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(AccountHint));
        OnPropertyChanged(nameof(JavaText));
        OnPropertyChanged(nameof(SharedSizeText));
        OnPropertyChanged(nameof(Greeting));
    }

    public async Task PlayAsync(InstanceProfile instance)
    {
        await InstancesViewModel.LaunchInstanceAsync(instance, _main);
        Refresh();
    }

    public void OpenMods(InstanceProfile instance) => InstancesViewModel.OpenModsWindow(instance);

    private async Task PlayLastAsync()
    {
        if (LastPlayed is null) return;
        await PlayAsync(LastPlayed);
    }

    private void OpenModsForLast()
    {
        if (LastPlayed is null) return;
        OpenMods(LastPlayed);
    }

    private void GoTo(string key)
    {
        var page = _main.Pages.FirstOrDefault(p => p.Key == key);
        if (page is not null) _main.SelectedPage = page;
    }
}