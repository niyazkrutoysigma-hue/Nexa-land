using System.Text.Json.Serialization;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

public enum AppTheme { Dark, Light }

/// <summary>Настройки приложения (settings.json).</summary>
public sealed class SettingsService : ObservableObject
{
    public static SettingsService Default { get; } = new();

    // Публичный конструктор нужен System.Text.Json для загрузки settings.json
    public SettingsService()
    {
    }

    private AppTheme _theme = AppTheme.Dark;
    private string _accentColor = "#8B5CF6";
    private string _instancesFolder = string.Empty;
    private int _defaultRamMb = 4096;
    private int _minRamMb = 512;
    private string _javaPath = "";
    // Общий публичный ключ CurseForge (используется PolyMC/Prism Launcher).
// Пользователь может заменить его на свой в настройках.
private string _curseForgeApiKey = "$2a$10$bL4bIL5pUWqfcO7KQtnMReakwtfHbNKh6v1uTpKlzhwoueEJQnPnm";
    private string _selfUpdateRepo = "";
    private bool _autoInstallJava = true;
    private bool _autoUpdateLauncher = true;
    private bool _closeAfterLaunch = false;
    private bool _showSnapshots = false;
    private string _launcherBrand = "NEXA LAND";
    private string _msaClientId = "00000000402b5328";

    [JsonPropertyName("theme")] public AppTheme Theme { get => _theme; set { SetProperty(ref _theme, value); } }
    [JsonPropertyName("accent")] public string AccentColor { get => _accentColor; set { SetProperty(ref _accentColor, value); } }
    [JsonPropertyName("instancesFolder")] public string InstancesFolder { get => _instancesFolder; set => SetProperty(ref _instancesFolder, value); }
    [JsonPropertyName("defaultRam")] public int DefaultRamMb { get => _defaultRamMb; set => SetProperty(ref _defaultRamMb, value); }
    [JsonPropertyName("minRam")] public int MinRamMb { get => _minRamMb; set => SetProperty(ref _minRamMb, value); }
    [JsonPropertyName("javaPath")] public string JavaPath { get => _javaPath; set => SetProperty(ref _javaPath, value); }
    [JsonPropertyName("cfApiKey")] public string CurseForgeApiKey { get => _curseForgeApiKey; set => SetProperty(ref _curseForgeApiKey, value); }
    [JsonPropertyName("selfUpdateRepo")] public string SelfUpdateRepo { get => _selfUpdateRepo; set => SetProperty(ref _selfUpdateRepo, value); }
    [JsonPropertyName("autoInstallJava")] public bool AutoInstallJava { get => _autoInstallJava; set => SetProperty(ref _autoInstallJava, value); }
    [JsonPropertyName("autoUpdateLauncher")] public bool AutoUpdateLauncher { get => _autoUpdateLauncher; set => SetProperty(ref _autoUpdateLauncher, value); }
    [JsonPropertyName("closeAfterLaunch")] public bool CloseAfterLaunch { get => _closeAfterLaunch; set => SetProperty(ref _closeAfterLaunch, value); }
    [JsonPropertyName("showSnapshots")] public bool ShowSnapshots { get => _showSnapshots; set => SetProperty(ref _showSnapshots, value); }
    [JsonPropertyName("brand")] public string LauncherBrand { get => _launcherBrand; set => SetProperty(ref _launcherBrand, value); }
    [JsonPropertyName("msaClientId")] public string MsaClientId { get => _msaClientId; set => SetProperty(ref _msaClientId, value); }

    [JsonIgnore]
    public string EffectiveInstancesFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstancesFolder)) return InstancesFolder;
            var path = Path.Combine(AppPaths.Root, "instances");
            AppPaths.Ensure(path);
            return path;
        }
    }

    [JsonIgnore]
    public int TotalRamMb
    {
        get
        {
            try
            {
                return (int)(SystemMemory.TotalPhysicalBytes / 1024 / 1024);
            }
            catch
            {
                return 8192;
            }
        }
    }

    public void Load()
    {
        var loaded = Json.ReadFile<SettingsService>(AppPaths.SettingsFile);
        if (loaded is null)
        {
            Save();
            return;
        }

        _theme = loaded._theme;
        _accentColor = loaded._accentColor;
        _instancesFolder = loaded._instancesFolder;
        _defaultRamMb = loaded._defaultRamMb;
        _minRamMb = loaded._minRamMb;
        _javaPath = loaded._javaPath;
        _curseForgeApiKey = loaded._curseForgeApiKey;
        _selfUpdateRepo = loaded._selfUpdateRepo;
        _autoInstallJava = loaded._autoInstallJava;
        _autoUpdateLauncher = loaded._autoUpdateLauncher;
        _closeAfterLaunch = loaded._closeAfterLaunch;
        _showSnapshots = loaded._showSnapshots;
        _launcherBrand = loaded._launcherBrand;
        _msaClientId = loaded._msaClientId;

        AppPaths.Ensure(EffectiveInstancesFolder);
        Raise(nameof(Theme), nameof(AccentColor), nameof(InstancesFolder), nameof(DefaultRamMb),
            nameof(MinRamMb), nameof(JavaPath), nameof(CurseForgeApiKey), nameof(SelfUpdateRepo),
            nameof(AutoInstallJava), nameof(AutoUpdateLauncher), nameof(CloseAfterLaunch),
            nameof(ShowSnapshots), nameof(LauncherBrand), nameof(MsaClientId));
    }

    public void Save()
    {
        try
        {
            Json.WriteFile(AppPaths.SettingsFile, this);
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Не удалось сохранить настройки", ex);
        }
    }
}

/// <summary>Список инстансов (instances.json).</summary>
public sealed class InstanceService
{
    private readonly List<InstanceProfile> _instances = [];

    public IReadOnlyList<InstanceProfile> Instances => _instances;

    public void Load()
    {
        _instances.Clear();
        var loaded = Json.ReadFile<List<InstanceProfile>>(AppPaths.InstancesFile);
        if (loaded is not null) _instances.AddRange(loaded);
        LogService.Default.Info($"Загружено инстансов: {_instances.Count}");
    }

    public void Save()
    {
        try
        {
            // Страховка от потери сборок: если список внезапно пуст, а в файле есть записи,
            // значит список не был загружен (или читался с ошибкой) — перетирать файл нельзя.
            if (_instances.Count == 0 && File.Exists(AppPaths.InstancesFile))
            {
                var existing = Json.ReadFile<List<InstanceProfile>>(AppPaths.InstancesFile);
                if (existing is { Count: > 0 })
                {
                    LogService.Default.Error(
                        $"Список сборок пуст, в файле {existing.Count} — сохранение отменено, чтобы не потерять сборки");
                    return;
                }
            }

            Json.WriteFile(AppPaths.InstancesFile, _instances);
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Не удалось сохранить список инстансов", ex);
        }
    }

    public InstanceProfile Add(InstanceProfile profile)
    {
        profile.EnsureFolders();
        _instances.Add(profile);
        Save();
        return profile;
    }

    public void Remove(InstanceProfile profile)
    {
        _instances.Remove(profile);
        Save();
        try
        {
            if (Directory.Exists(profile.RootDir))
                Directory.Delete(profile.RootDir, recursive: true);
            LogService.Default.Info($"Инстанс «{profile.Name}» удалён");
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось удалить папку инстанса: {ex.Message}");
        }
    }

    public void Update(InstanceProfile profile) => Save();

    public InstanceProfile? Find(string id) => _instances.FirstOrDefault(i => i.Id == id);
}