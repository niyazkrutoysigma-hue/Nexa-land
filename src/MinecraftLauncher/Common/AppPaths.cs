namespace MinecraftLauncher.Common;

/// <summary>Все пути приложения. Хранится в %AppData%\NexaLand.</summary>
public static class AppPaths
{
    public const string Brand = "NEXA LAND";
    public const string AppFolderName = "NexaLand";
    private const string LegacyFolderName = "MinecraftLauncher";

    public static string Root { get; } = ResolveRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AccountsFile => Path.Combine(Root, "accounts.dat");
    public static string InstancesFile => Path.Combine(Root, "instances.json");
    public static string SharedStore => Ensure(Path.Combine(Root, "shared"));
    public static string SharedAssets => Ensure(Path.Combine(SharedStore, "assets"));
    public static string SharedVersions => Ensure(Path.Combine(SharedStore, "versions"));
    public static string SharedLibraries => Ensure(Path.Combine(SharedStore, "libraries"));
    public static string JavaRoot => Ensure(Path.Combine(Root, "java"));
    public static string LogsDir => Ensure(Path.Combine(Root, "logs"));
    public static string CacheDir => Ensure(Path.Combine(Root, "cache"));
    public static string TempDir => Ensure(Path.Combine(CacheDir, "temp"));
    public static string DownloadsDir => Ensure(Path.Combine(CacheDir, "downloads"));
    public static string ImageCacheDir => Ensure(Path.Combine(CacheDir, "images"));
    public static string LogoFile => Path.Combine(Root, "logo.png");

    /// <summary>Переносит данные прежней сборки в новую папку, чтобы не терять скачанные версии.</summary>
    private static string ResolveRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = Path.Combine(appData, AppFolderName);
        var legacy = Path.Combine(appData, LegacyFolderName);

        if (!Directory.Exists(root) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, root);
                LogService.Default.Info($"Данные перенесены в папку {AppFolderName}");
            }
            catch (Exception ex)
            {
                LogService.Default.Warn($"Не удалось перенести данные: {ex.Message}");
            }
        }

        return Ensure(root);
    }

    public static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { /* путь может быть недоступен — вызывающий код разберётся */ }
        return path;
    }
}