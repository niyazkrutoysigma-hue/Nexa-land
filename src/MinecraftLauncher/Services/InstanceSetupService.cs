using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>
/// Готовит сборку к запуску: доустанавливает загрузчик, файлы версии и Java.
/// Это единая точка входа и для кнопки «Играть», и для мастера создания сборки.
/// </summary>
public sealed class InstanceSetupService
{
    private readonly LaunchService _launch;
    private readonly VersionInstaller _installer;
    private readonly LoaderService _loaders;
    private readonly SettingsService _settings = SettingsService.Default;

    public InstanceSetupService(LaunchService launch, VersionInstaller installer, LoaderService loaders)
    {
        _launch = launch;
        _installer = installer;
        _loaders = loaders;
    }

    /// <summary>Возвращает готовый к запуску version.json, попутно докачивая всё необходимое.</summary>
    public async Task<VersionJson> EnsureReadyAsync(InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        instance.EnsureFolders();

        // 0. Сборка могла остаться без ссылки на version.json (например, созданная вручную
        //    или после смены загрузчика) — тогда version.json и библиотеки не качаются,
        //    а игра падает с NoClassDefFoundError. Восстанавливаем ссылку из манифеста.
        await TryResolveVersionUrlAsync(instance, ct);

        // 1. Загрузчик модов ещё не установлен
        if (instance.Loader != LoaderType.None && string.IsNullOrWhiteSpace(instance.VersionUrl))
        {
            LogService.Default.Info($"Устанавливаем {instance.LoaderVersionText} для сборки «{instance.Name}»");
            await _loaders.InstallLoaderAsync(instance, instance.Loader, instance.LoaderVersion, progress, ct);
            _settings.Save();
        }

        // 2. Файлы версии, библиотеки, ассеты
        var version = await _launch.ResolveVersionAsync(instance, ct);
        var target = InstallTarget.For(instance);

        if (!_installer.IsInstalled(version, target))
        {
            LogService.Default.Info($"Докачиваем файлы версии {instance.DisplayVersion}");
            await _installer.InstallAsync(version, target, progress,
                includeClient: version.Downloads?.Client?.Url is not null, ct);
        }

        // 3. Java
        await EnsureJavaAsync(instance, version, progress, ct);

        return version;
    }

    public async Task EnsureJavaAsync(InstanceProfile instance, VersionJson version,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var required = version.JavaMajor > 0 ? version.JavaMajor : JavaRequirements.For(instance.MinecraftVersion);

        try
        {
            _launch.ResolveJavaPath(instance, version);
            return;
        }
        catch (JavaNotFoundException)
        {
            if (!_settings.AutoInstallJava) throw;
        }

        LogService.Default.Info($"Java {required} не найдена — скачиваем Temurin");
        await _launch.EnsureJavaAsync(required, progress, ct);
    }

    /// <summary>
    /// Восстанавливает ссылку на version.json по манифесту Mojang, если она потерялась.
    /// Возвращает false, если версию в манифесте найти не удалось.
    /// </summary>
    public async Task<bool> TryResolveVersionUrlAsync(InstanceProfile instance, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(instance.VersionUrl)) return true;
        if (instance.Loader != LoaderType.None) return false; // загрузчик ставит свою ссылку

        try
        {
            var manifest = await AppServices.Mojang.GetManifestAsync(false, ct);
            var summary = MojangApi.Find(manifest, instance.MinecraftVersion);
            if (summary is null) return false;

            instance.VersionUrl = summary.Url;
            instance.VersionJsonId = summary.Id;
            LogService.Default.Info($"Восстановлена ссылка на version.json {summary.Id}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось найти {instance.MinecraftVersion} в манифесте: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Простой контейнер сервисов (вместо полноценного DI).</summary>
public static class AppServices
{
    public static ApiClient Api { get; private set; } = null!;
    public static VersionMetadataService Metadata { get; private set; } = null!;
    public static MojangApi Mojang { get; private set; } = null!;
    public static VersionInstaller Installer { get; private set; } = null!;
    public static JavaLocator Java { get; private set; } = null!;
    public static AdoptiumService Adoptium { get; private set; } = null!;
    public static LoaderService Loaders { get; private set; } = null!;
    public static LaunchService Launch { get; private set; } = null!;
    public static InstanceSetupService Setup { get; private set; } = null!;
    public static InstanceService Instances { get; private set; } = null!;
    public static AccountService Accounts { get; private set; } = null!;
    public static MicrosoftAuthService Auth { get; private set; } = null!;
    public static ModrinthService Modrinth { get; private set; } = null!;
    public static CurseForgeService CurseForge { get; private set; } = null!;
    public static ModpackService Modpacks { get; private set; } = null!;
    public static IconService Icons { get; private set; } = null!;
    public static SelfUpdateService Updates { get; private set; } = null!;
    public static SettingsService Settings => SettingsService.Default;
    public static IDialogService Dialogs { get; set; } = null!;

    public static void Initialize()
    {
        Api = new ApiClient();
        Metadata = new VersionMetadataService(Api);
        Mojang = new MojangApi(Api);
        Installer = new VersionInstaller(Api, Metadata);
        Java = new JavaLocator();
        Adoptium = new AdoptiumService(Api);
        Accounts = new AccountService();
        Auth = new MicrosoftAuthService(Api);
        Launch = new LaunchService(Api, Metadata, Java, Adoptium, Accounts);
        Loaders = new LoaderService(Api, Installer, Metadata, Launch, Java);
        Setup = new InstanceSetupService(Launch, Installer, Loaders);
        Instances = new InstanceService();
        Modrinth = new ModrinthService(Api);
        CurseForge = new CurseForgeService(Api);
        Modpacks = new ModpackService(Modrinth);
        Icons = new IconService(Api);
        Updates = new SelfUpdateService(Api);
    }
}