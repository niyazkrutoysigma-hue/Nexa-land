using System.Text.Json.Serialization;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Models;

public enum LoaderType
{
    None,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    OptiFine
}

public static class LoaderInfo
{
    public static string Display(LoaderType type) => type switch
    {
        LoaderType.Fabric => "Fabric",
        LoaderType.Quilt => "Quilt",
        LoaderType.Forge => "Forge",
        LoaderType.NeoForge => "NeoForge",
        LoaderType.OptiFine => "OptiFine",
        _ => "Ванильный"
    };

    public static string ModrinthId(LoaderType type) => type switch
    {
        LoaderType.Fabric => "fabric",
        LoaderType.Quilt => "quilt",
        LoaderType.Forge => "forge",
        LoaderType.NeoForge => "neoforge",
        _ => ""
    };
}

/// <summary>Инстанс = отдельная копия игры со своей папкой, модами и настройками.</summary>
public sealed class InstanceProfile : ObservableObject
{
    private string _name = "Новый инстанс";
    private LoaderType _loader = LoaderType.None;
    private string _loaderVersion = "";
    private string _javaPath = "";
    private int _ramMb = 2048;
    private string _javaArgs = "";
    private string _minecraftArgs = "";
    private bool _isolated;
    private bool _hidden;
    private string? _lastError;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")] public string Name { get => _name; set => SetProperty(ref _name, value); }

    [JsonPropertyName("group")] public string Group { get; set; } = "net.minecraft";

    [JsonPropertyName("mcVersion")] public string MinecraftVersion { get; set; } = "";

    /// <summary>Ссылка на version.json (ванильная версия или профиль загрузчика).</summary>
    [JsonPropertyName("mcUrl")] public string VersionUrl { get; set; } = "";

    /// <summary>Имя каталога с version.json в папке инстанса.</summary>
    [JsonPropertyName("mcJsonId")] public string VersionJsonId { get; set; } = "";

    [JsonPropertyName("mcType")] public string VersionType { get; set; } = "release";

    [JsonPropertyName("releaseTime")] public DateTime ReleaseTime { get; set; }

    [JsonPropertyName("loader")] public LoaderType Loader { get => _loader; set { SetProperty(ref _loader, value); OnPropertyChanged(nameof(LoaderName)); OnPropertyChanged(nameof(LoaderVersionText)); } }

    [JsonPropertyName("loaderVersion")] public string LoaderVersion { get => _loaderVersion; set { SetProperty(ref _loaderVersion, value); OnPropertyChanged(nameof(LoaderVersionText)); } }

    [JsonPropertyName("javaPath")] public string JavaPath { get => _javaPath; set => SetProperty(ref _javaPath, value); }

    [JsonPropertyName("ramMb")] public int RamMb { get => _ramMb; set => SetProperty(ref _ramMb, value); }

    [JsonPropertyName("javaArgs")] public string JavaArgs { get => _javaArgs; set => SetProperty(ref _javaArgs, value); }

    [JsonPropertyName("minecraftArgs")] public string MinecraftArgs { get => _minecraftArgs; set => SetProperty(ref _minecraftArgs, value); }

    /// <summary>Свои libraries/versions (нужно Forge/NeoForge и Fabric с модами).</summary>
    [JsonPropertyName("isolated")] public bool Isolated { get => _isolated; set => SetProperty(ref _isolated, value); }

    [JsonPropertyName("hidden")] public bool Hidden { get => _hidden; set => SetProperty(ref _hidden, value); }

    [JsonIgnore] public DateTime? LastPlayed { get; set; }

    [JsonIgnore] public string? LastError { get => _lastError; set => SetProperty(ref _lastError, value); }

    [JsonIgnore] public string IconPath { get; private set; } = "";

    [JsonIgnore]
    public string LoaderName => LoaderInfo.Display(Loader);

    [JsonIgnore]
    public string LoaderVersionText => Loader == LoaderType.None
        ? "Ванильный Minecraft"
        : $"{LoaderName} {LoaderVersion}".Trim();

    [JsonIgnore]
    public string RootDir => Path.Combine(SettingsService.Default.EffectiveInstancesFolder, Id);

    [JsonIgnore]
    public string GameDir => Path.Combine(RootDir, "mc");

    [JsonIgnore]
    public string IconFile => Path.Combine(RootDir, "icon.png");

    [JsonIgnore]
    public string ModsDir => Path.Combine(GameDir, "mods");

    [JsonIgnore]
    public string VersionsRoot => Isolated ? Path.Combine(RootDir, "versions") : AppPaths.SharedVersions;

    [JsonIgnore]
    public string LibraryRoot => Isolated ? Path.Combine(RootDir, "libraries") : AppPaths.SharedLibraries;

    [JsonIgnore]
    public string AssetsRoot => AppPaths.SharedAssets;

    /// <summary>
    /// Путь к клиентскому jar. У загрузчиков класс грузится из папки ванильной версии,
    /// поэтому jar всегда лежит там же, где его положил бы vanilla launcher.
    /// </summary>
    [JsonIgnore]
    public string ClientJarPath => Loader == LoaderType.None
        ? Path.Combine(VersionsRoot, VersionJsonId.Length > 0 ? VersionJsonId : MinecraftVersion,
            (VersionJsonId.Length > 0 ? VersionJsonId : MinecraftVersion) + ".jar")
        : Path.Combine(VersionsRoot, MinecraftVersion, MinecraftVersion + ".jar");

    /// <summary>Отображаемое имя версии.</summary>
    [JsonIgnore]
    public string DisplayVersion => MinecraftVersion +
        (Loader == LoaderType.None ? "" : $" {LoaderName}");

    [JsonIgnore]
    public string GroupLabel => Group switch
    {
        "net.fabricmc" => "Fabric",
        "net.quiltmc" => "Quilt",
        "net.minecraftforge" => "Forge",
        "net.neoforged" => "NeoForge",
        _ => "Minecraft"
    };

    [JsonIgnore]
    public string FolderInfo
    {
        get
        {
            var dir = new DirectoryInfo(GameDir);
            if (!dir.Exists) return "—";
            long size = 0;
            try { size = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { }
            return DownloadStatus.Format(size);
        }
    }

    [JsonIgnore]
    public int EnabledModsCount
    {
        get
        {
            try
            {
                return Directory.Exists(ModsDir)
                    ? Directory.GetFiles(ModsDir, "*.jar").Length
                    : 0;
            }
            catch { return 0; }
        }
    }

    public void EnsureFolders()
    {
        Common.AppPaths.Ensure(RootDir);
        Common.AppPaths.Ensure(GameDir);
        Common.AppPaths.Ensure(ModsDir);
        Common.AppPaths.Ensure(Path.Combine(GameDir, "config"));
        Common.AppPaths.Ensure(Path.Combine(GameDir, "saves"));
        Common.AppPaths.Ensure(Path.Combine(GameDir, "resourcepacks"));
        Common.AppPaths.Ensure(Path.Combine(GameDir, "shaderpacks"));
        Common.AppPaths.Ensure(Path.Combine(GameDir, "screenshots"));
        Common.AppPaths.Ensure(Path.Combine(GameDir, "logs"));
        if (Isolated)
        {
            Common.AppPaths.Ensure(Path.Combine(RootDir, "versions"));
            Common.AppPaths.Ensure(Path.Combine(RootDir, "libraries"));
        }
    }
}

/// <summary>Игровой аккаунт.</summary>
public sealed class Account : ObservableObject
{
    private bool _isActive;
    private string? _lastError;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("type")] public string Type { get; init; } = "Offline";

    [JsonPropertyName("name")] public string DisplayName { get; set; } = "Игрок";

    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";

    [JsonPropertyName("avatar")] public string? AvatarUrl { get; set; }

    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }

    [JsonPropertyName("xuid")] public string? Xuid { get; set; }

    [JsonPropertyName("msaClientId")] public string? MsaClientId { get; set; }

    [JsonIgnore] public bool IsMicrosoft => Type == "Microsoft";

    [JsonPropertyName("active")] public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    [JsonIgnore] public string? LastError { get => _lastError; set => SetProperty(ref _lastError, value); }

    [JsonIgnore]
    public string TypeLabel => IsMicrosoft ? "Microsoft" : "Оффлайн";
}