using System.Collections.ObjectModel;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;
using MinecraftLauncher.Services;

namespace MinecraftLauncher.ViewModels;

/// <summary>Мастер создания/правки инстанса.</summary>
public sealed class InstanceEditorViewModel : ObservableObject
{
    private readonly RelayCommand _saveCommand;
    private string _name = "Новый инстанс";
    private string _search = string.Empty;
    private bool _showSnapshots;
    private VersionSummary? _selectedVersion;
    private LoaderType _loader = LoaderType.None;
    private string _loaderVersion = "";
    private int _ram = SettingsService.Default.DefaultRamMb;
    private string _javaPath = "";
    private string _javaArgs = "";
    private string _minecraftArgs = "";
    private bool _isolated;
    private string _loaderHint = "Загрузчик модов не выбран";
    private bool _loadingLoaders;

    public InstanceEditorViewModel(InstanceProfile? existing)
    {
        _saveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(() => Result = null);
        RefreshCommand = new AsyncRelayCommand(() => LoadVersionsAsync(force: true));
        SearchCommand = new RelayCommand(ApplyFilter);

        Loaders =
        [
            LoaderType.None, LoaderType.Fabric, LoaderType.Quilt,
            LoaderType.Forge, LoaderType.NeoForge
        ];

        if (existing is not null)
        {
            Id = existing.Id;
            _name = existing.Name;
            _loader = existing.Loader;
            _loaderVersion = existing.LoaderVersion;
            _ram = existing.RamMb;
            _javaPath = existing.JavaPath;
            _javaArgs = existing.JavaArgs;
            _minecraftArgs = existing.MinecraftArgs;
            _isolated = existing.Isolated;
            _selectedVersion = new VersionSummary
            {
                Id = existing.MinecraftVersion,
                Type = existing.VersionType,
                Url = existing.VersionUrl,
                ReleaseTime = existing.ReleaseTime
            };
            Existing = existing;
        }

        IconPath = existing?.IconFile ?? "";
    }

    private string? Id { get; set; }
    private InstanceProfile? Existing { get; }

    public ObservableCollection<VersionSummary> AllVersions { get; } = [];
    public ObservableCollection<VersionSummary> Versions { get; } = [];
    public ObservableCollection<LoaderType> Loaders { get; }
    public ObservableCollection<LoaderVersion> LoaderVersions { get; } = [];

    /// <summary>Результат мастера: null — отмена.</summary>
    public InstanceProfile? Result { get; set; }

    public RelayCommand SaveCommand => _saveCommand;
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SearchCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) _saveCommand.RaiseCanExecuteChanged();
        }
    }

    public string Search
    {
        get => _search;
        set => SetProperty(ref _search, value);
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set => SetProperty(ref _showSnapshots, value);
    }

    public VersionSummary? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;

            if (value is not null && string.IsNullOrWhiteSpace(_name))
                Name = value.Id;

            _loaderVersion = "";
            LoaderVersions.Clear();
            OnPropertyChanged(nameof(LoaderVersion));
            _saveCommand.RaiseCanExecuteChanged();
            _ = LoadLoaderVersionsAsync();
        }
    }

    public LoaderType SelectedLoader
    {
        get => _loader;
        set
        {
            if (!SetProperty(ref _loader, value)) return;

            LoaderVersions.Clear();
            _loaderVersion = "";
            OnPropertyChanged(nameof(LoaderVersion));
            LoaderHint = value == LoaderType.None
                ? "Загрузчик модов не выбран"
                : "Загрузка доступных версий…";
            _ = LoadLoaderVersionsAsync();
        }
    }

    public string LoaderVersion
    {
        get => _loaderVersion;
        set
        {
            if (SetProperty(ref _loaderVersion, value)) _saveCommand.RaiseCanExecuteChanged();
        }
    }

    public string LoaderHint
    {
        get => _loaderHint;
        private set => SetProperty(ref _loaderHint, value);
    }

    public int Ram
    {
        get => _ram;
        set
        {
            if (SetProperty(ref _ram, Math.Clamp(value, 512, SettingsService.Default.TotalRamMb - 512)))
                OnPropertyChanged(nameof(RamText));
        }
    }

    public string RamText => Ram >= 1024 ? $"{Ram / 1024.0:0.#} ГБ" : $"{Ram} МБ";

    public int MaxRam => Math.Max(2048, SettingsService.Default.TotalRamMb - 1024);

    public string JavaPath
    {
        get => _javaPath;
        set => SetProperty(ref _javaPath, value);
    }

    public string JavaArgs
    {
        get => _javaArgs;
        set => SetProperty(ref _javaArgs, value);
    }

    public string MinecraftArgs
    {
        get => _minecraftArgs;
        set => SetProperty(ref _minecraftArgs, value);
    }

    public bool Isolated
    {
        get => _isolated;
        set => SetProperty(ref _isolated, value);
    }

    public string IconPath { get; }

    public bool CanSave => !string.IsNullOrWhiteSpace(Name)
                           && SelectedVersion is not null
                           && (SelectedLoader == LoaderType.None || !string.IsNullOrWhiteSpace(LoaderVersion));

    public bool IsEdit => Existing is not null;

    public async Task LoadVersionsAsync(bool force = false)
    {
        try
        {
            var manifest = await AppServices.Mojang.GetManifestAsync(force);
            AllVersions.Clear();
            foreach (var version in ShowSnapshots
                         ? MojangApi.All(manifest)
                         : MojangApi.Releases(manifest))
            {
                AllVersions.Add(version);
            }

            ApplyFilter();

            if (SelectedVersion is not null &&
                AllVersions.FirstOrDefault(v => v.Id == SelectedVersion!.Id) is { } match)
            {
                _selectedVersion = match;
                OnPropertyChanged(nameof(SelectedVersion));
            }
            else if (SelectedVersion is null && AllVersions.Count > 0)
            {
                SelectedVersion = AllVersions[0];
            }
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Список версий не загружен", ex);
        }
    }

    private void ApplyFilter()
    {
        Versions.Clear();
        var query = Search.Trim();
        foreach (var version in AllVersions.Where(v =>
                     string.IsNullOrEmpty(query) || v.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            Versions.Add(version);
        }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        if (_loadingLoaders) return;
        if (SelectedLoader == LoaderType.None || SelectedVersion is null) return;

        _loadingLoaders = true;
        try
        {
            var versions = await AppServices.Loaders.GetLoaderVersionsAsync(SelectedLoader, SelectedVersion.Id);
            foreach (var version in versions) LoaderVersions.Add(version);

            if (versions.Count > 0)
            {
                LoaderVersion = versions[0].Version;
                LoaderHint = $"{versions.Count} версий, выбрана {versions[0].Version}";
            }
            else
            {
                LoaderHint = "Список версий пуст — впишите версию вручную";
            }
        }
        catch (Exception ex)
        {
            LoaderHint = "Не удалось получить версии: " + ex.Message;
        }
        finally
        {
            _loadingLoaders = false;
            _saveCommand.RaiseCanExecuteChanged();
        }
    }

    private void Save()
    {
        if (!CanSave || SelectedVersion is null) return;

        var group = SelectedLoader switch
        {
            LoaderType.Fabric => "net.fabricmc",
            LoaderType.Quilt => "net.quiltmc",
            LoaderType.Forge => "net.minecraftforge",
            LoaderType.NeoForge => "net.neoforged",
            _ => "net.minecraft"
        };

        // Правим существующий инстанс на месте, чтобы не терять привязки в UI
        var target = Existing ?? new InstanceProfile();

        target.Name = Name.Trim();
        target.Group = group;
        target.MinecraftVersion = SelectedVersion.Id;
        target.VersionType = SelectedVersion.Type;
        target.ReleaseTime = SelectedVersion.ReleaseTime;
        target.Loader = SelectedLoader;
        target.LoaderVersion = LoaderVersion.Trim();
        target.RamMb = Ram;
        target.JavaPath = JavaPath.Trim();
        target.JavaArgs = JavaArgs.Trim();
        target.MinecraftArgs = MinecraftArgs.Trim();
        target.Isolated = Isolated || SelectedLoader != LoaderType.None;

        if (Existing is null)
        {
            // Для ванилы сразу прописываем url версии — установка начнётся при запуске
            if (SelectedLoader == LoaderType.None)
            {
                target.VersionUrl = SelectedVersion.Url;
                target.VersionJsonId = SelectedVersion.Id;
            }
        }
        else if (SelectedLoader != Existing.Loader)
        {
            // Смена загрузчика требует переустановки
            target.VersionUrl = SelectedLoader == LoaderType.None ? SelectedVersion.Url : string.Empty;
            target.VersionJsonId = SelectedLoader == LoaderType.None ? SelectedVersion.Id : string.Empty;
        }

        Result = target;
    }
}