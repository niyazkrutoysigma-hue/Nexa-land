using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Каталог релизов и снапшотов с установкой в один клик.</summary>
public sealed class StoreViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private VersionSummary? _selected;
    private string _search = string.Empty;
    private bool _showSnapshots;
    private bool _loaded;
    private string _latestInfo = string.Empty;
    private CancellationTokenSource? _cts;

    public StoreViewModel(MainViewModel main)
    {
        _main = main;
        _showSnapshots = SettingsService.Default.ShowSnapshots;

        InstallCommand = new AsyncRelayCommand(() => InstallSelectedAsync(false));
        InstallWithLoaderCommand = new AsyncRelayCommand(() => InstallSelectedAsync(true));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsInstalling);
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true));
    }

    public ObservableCollection<VersionSummary> Versions { get; } = [];

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallWithLoaderCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsInstalling { get; private set; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) ApplyFilter();
        }
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (!SetProperty(ref _showSnapshots, value)) return;

            SettingsService.Default.ShowSnapshots = value;
            SettingsService.Default.Save();

            _all = [];
            ApplyFilter();
        }
    }

    public string LatestInfo
    {
        get => _latestInfo;
        private set => SetProperty(ref _latestInfo, value);
    }

    public VersionSummary? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force) return;
        _loaded = true;

        await _main.RunAsync("Загружаем список версий…", async () =>
        {
            try
            {
                var manifest = await AppServices.Mojang.GetManifestAsync(force);
                LatestInfo = $"Актуальный релиз: {manifest.Latest.Release} · свежий снапшот: {manifest.Latest.Snapshot}";
                ApplyFilter(manifest);
            }
            finally
            {
                _loaded = !force;
            }
        });
    }

    private void ApplyFilter(VersionManifest? manifest = null)
    {
        if (manifest is null)
        {
            // локальная фильтрация уже загруженного списка
            ApplyFilterCore();
            return;
        }

        _all = ShowSnapshots
            ? MojangApi.All(manifest).ToList()
            : MojangApi.Releases(manifest).ToList();

        ApplyFilterCore();
    }

    private List<VersionSummary> _all = [];

    private void ApplyFilterCore()
    {
        var query = Search.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        Versions.Clear();
        foreach (var version in filtered) Versions.Add(version);
    }

    private async Task InstallSelectedAsync(bool withLoader)
    {
        if (Selected is null) return;

        var version = Selected;
        if (withLoader)
        {
            var draft = new InstanceProfile
            {
                Name = version.Id + " сборка",
                MinecraftVersion = version.Id,
                VersionType = version.Type,
                ReleaseTime = version.ReleaseTime,
                RamMb = SettingsService.Default.DefaultRamMb,
                Group = "net.minecraft"
            };

            var edited = AppServices.Dialogs.ShowInstanceEditor(draft);
            if (edited is null) return;

            AppServices.Instances.Add(edited);
            AppServices.Icons.GenerateInstanceIcon(edited);
            await InstallAsync(edited);
            return;
        }

        var instance = new InstanceProfile
        {
            Name = version.Id,
            MinecraftVersion = version.Id,
            VersionType = version.Type,
            ReleaseTime = version.ReleaseTime,
            RamMb = SettingsService.Default.DefaultRamMb,
            Group = "net.minecraft"
        };

        AppServices.Instances.Add(instance);
        AppServices.Icons.GenerateInstanceIcon(instance);
        await InstallAsync(instance);
    }

    private async Task InstallAsync(InstanceProfile instance)
    {
        IsInstalling = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancelCommand.RaiseCanExecuteChanged();

        var progress = new Progress<InstallProgress>(_main.ReportProgress);

        await _main.RunAsync($"Устанавливаем {instance.DisplayVersion}…", async () =>
        {
            try
            {
                // Узнаём url версии и готовим инстанс
                var manifest = await AppServices.Mojang.GetManifestAsync(false, _cts.Token);
                var summary = MojangApi.Find(manifest, instance.MinecraftVersion)
                    ?? throw new FileNotFoundException($"Версия {instance.MinecraftVersion} не найдена в манифесте");

                instance.VersionUrl = summary.Url;
                instance.VersionJsonId = summary.Id;

                await AppServices.Setup.EnsureReadyAsync(instance, progress, _cts.Token);
                AppServices.Instances.Save();

                _main.ReportText($"{instance.DisplayVersion} установлена");
            }
            catch (Exception)
            {
                // установка не удалась — инстанс оставляем, файлы докачаются при запуске
                AppServices.Instances.Save();
                throw;
            }
            finally
            {
                IsInstalling = false;
                CancelCommand.RaiseCanExecuteChanged();
            }
        });
    }
}