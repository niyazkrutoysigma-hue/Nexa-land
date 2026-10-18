using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

public sealed record LoaderVersion(string Version, string Name);

/// <summary>Установка Fabric/Quilt/Forge/NeoForge в инстанс.</summary>
public sealed class LoaderService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3";
    private const string ForgeMetaUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    private const string NeoForgeMetaUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string ForgeInstallerBase = "https://maven.minecraftforge.net/net/minecraftforge/forge/{0}/{0}-installer.jar";
    private const string NeoForgeInstallerBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge/{0}/neoforge-{0}-installer.jar";

    private sealed class FabricLoader
    {
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private sealed class QuiltLoader
    {
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    }

    private readonly ApiClient _api;
    private readonly VersionInstaller _installer;
    private readonly VersionMetadataService _metadata;
    private readonly LaunchService _launch;
    private readonly JavaLocator _javaLocator;

    public LoaderService(ApiClient api, VersionInstaller installer, VersionMetadataService metadata,
        LaunchService launch, JavaLocator javaLocator)
    {
        _api = api;
        _installer = installer;
        _metadata = metadata;
        _launch = launch;
        _javaLocator = javaLocator;
    }

    // ---------------- Выбор версий загрузчика ----------------

    public async Task<List<LoaderVersion>> GetLoaderVersionsAsync(LoaderType loader, string mcVersion,
        CancellationToken ct = default) => loader switch
    {
        LoaderType.Fabric => await FabricVersionsAsync(mcVersion, ct),
        LoaderType.Quilt => await QuiltVersionsAsync(mcVersion, ct),
        LoaderType.Forge => ForgeVersions(mcVersion),
        LoaderType.NeoForge => await NeoForgeVersionsAsync(mcVersion, ct),
        _ => []
    };

    private async Task<List<LoaderVersion>> FabricVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var raw = await _api.GetJsonAsync<List<FabricLoader>>($"{FabricMeta}/versions/loader/{mcVersion}", ct) ?? [];
        return raw.Select(v => new LoaderVersion(v.Version, v.Version + (v.Stable ? " (stable)" : ""))).ToList();
    }

    private async Task<List<LoaderVersion>> QuiltVersionsAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            var raw = await _api.GetJsonAsync<List<QuiltLoader>>($"{QuiltMeta}/versions/loader/{mcVersion}", ct) ?? [];
            return raw.Select(v => new LoaderVersion(v.Version, v.Version)).ToList();
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Quilt: не удалось получить список версий для {mcVersion} ({ex.Message})");
            return [];
        }
    }

    private List<LoaderVersion> ForgeVersions(string mcVersion)
    {
        try
        {
            var xml = _api.GetStringAsync(ForgeMetaUrl).GetAwaiter().GetResult();
            var all = XDocument.Parse(xml).Root!.Descendants("version").Select(v => v.Value).ToList();
            return all
                .Where(v => v.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(ParseForgeOrder)
                .Select(v => new LoaderVersion(v[(mcVersion.Length + 1)..], v))
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Forge: список версий не получен ({ex.Message})");
            return [];
        }
    }

    private async Task<List<LoaderVersion>> NeoForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var xml = await _api.GetStringAsync(NeoForgeMetaUrl, ct);
        var all = XDocument.Parse(xml).Root!.Descendants("version").Select(v => v.Value).ToList();

        // NeoForge использует собственную нумерацию: 1.21.4 -> 21.4.x, 1.20.6 -> 20.6.x и т.п.
        var prefixes = NeoForgePrefixes(mcVersion);
        var filtered = all.Where(v => prefixes.Any(p => v.StartsWith(p + ".", StringComparison.Ordinal)))
            .OrderByDescending(ParseForgeOrder)
            .Select(v => new LoaderVersion(v, $"NeoForge {v} для {mcVersion}"))
            .ToList();

        return filtered.Count > 0
            ? filtered
            : all.OrderByDescending(ParseForgeOrder).Take(30)
                .Select(v => new LoaderVersion(v, v)).ToList();
    }

    private static IEnumerable<string> NeoForgePrefixes(string mcVersion)
    {
        if (!Version.TryParse(mcVersion, out var v) || v.Major != 1) yield break;

        var prefix = $"{v.Minor}.{v.Build}";
        yield return prefix;

        // старые схемы: 1.20.1 -> 47.1.x, 1.20.4 -> 20.4.x
        if (v.Minor == 20 && v.Build == 1) yield return "47.1";
        if (v.Minor == 20 && v.Build == 2) yield return "20.2";
        if (v.Minor == 20 && v.Build == 3) yield return "20.3";
        if (v.Minor == 20 && v.Build == 4) yield return "20.4";
        if (v.Minor == 21 && v.Build == 0) yield return "21.0";

        // новая годовая схема: 1.21.10 -> 26.1.x
        if (v.Minor == 21 && v.Build >= 10) yield return $"26.{v.Build - 9}";
    }

    private static Version ParseForgeOrder(string value)
    {
        var parts = value.Split('-').Last().Split('.');
        var numbers = parts.Where(p => int.TryParse(p, out _)).Select(int.Parse).ToList();
        while (numbers.Count < 4) numbers.Add(0);
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    // ---------------- Установка ----------------

    /// <summary>Готовит инстанс под выбранный загрузчик и ставит его.</summary>
    public async Task InstallLoaderAsync(InstanceProfile instance, LoaderType loader, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        switch (loader)
        {
            case LoaderType.Fabric:
                await InstallFabricAsync(instance, loaderVersion, progress, ct);
                break;
            case LoaderType.Quilt:
                await InstallQuiltAsync(instance, loaderVersion, progress, ct);
                break;
            case LoaderType.Forge:
                await InstallForgeAsync(instance, loaderVersion, false, progress, ct);
                break;
            case LoaderType.NeoForge:
                await InstallForgeAsync(instance, loaderVersion, true, progress, ct);
                break;
        }

        instance.Loader = loader;
        instance.LoaderVersion = loaderVersion;
        instance.Isolated = true;
        instance.EnsureFolders();
    }

    private async Task InstallFabricAsync(InstanceProfile instance, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var url = $"{FabricMeta}/versions/loader/{instance.MinecraftVersion}/{loaderVersion}/profile/json";
        LogService.Default.Info($"Fabric {loaderVersion} для {instance.MinecraftVersion}");

        await InstallProfileAsync(instance, url, progress, ct);
    }

    private async Task InstallQuiltAsync(InstanceProfile instance, string loaderVersion,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var url = $"{QuiltMeta}/versions/loader/{instance.MinecraftVersion}/{loaderVersion}/profile/json";
        LogService.Default.Info($"Quilt {loaderVersion} для {instance.MinecraftVersion}");

        await InstallProfileAsync(instance, url, progress, ct);
    }

    /// <summary>Forge/NeoForge ставятся официальным установщиком в папку инстанса.</summary>
    private async Task InstallForgeAsync(InstanceProfile instance, string loaderVersion, bool neoForge,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        instance.Isolated = true;
        instance.EnsureFolders();

        var full = $"{instance.MinecraftVersion}-{loaderVersion}";
        var url = neoForge
            ? string.Format(NeoForgeInstallerBase, loaderVersion)
            : string.Format(ForgeInstallerBase, full);

        LogService.Default.Info($"{(neoForge ? "NeoForge" : "Forge")} {loaderVersion} — установщик {url}");

        var installerPath = Path.Combine(AppPaths.DownloadsDir, $"{(neoForge ? "neoforge" : "forge")}-{full}-installer.jar");
        progress?.Report(new InstallProgress("Загрузка установщика", 5, 0, 0, Path.GetFileName(installerPath)));
        await _api.DownloadFileAsync(url, installerPath, null, 0, null, ct, "Установщик");

        // Установщик требует Java >= своей версии: для старых Forge это 8, для NeoForge — 17+
        var requiredJava = Math.Max(8, JavaRequirements.For(instance.MinecraftVersion));
        var java = _javaLocator.FindBest(requiredJava);
        if (java is null)
        {
            var ensured = await _launch.EnsureJavaAsync(requiredJava, progress, ct);
            java = ensured;
        }

        progress?.Report(new InstallProgress($"Установка {LoaderInfo.Display(neoForge ? LoaderType.NeoForge : LoaderType.Forge)}", 20, 0, 0));

        var args = new List<string> { "-jar", installerPath, "--installClient", instance.RootDir };
        var exitCode = await RunInstallerAsync(java.Path, args, instance.RootDir, ct);
        if (exitCode != 0)
            throw new IOException($"Установщик завершился с кодом {exitCode}. Подробности в логе лаунчера.");

        // Установщик кладёт version.json с id вида "<mc>-<loader>" — находим самый свежий
        var installedId = FindInstalledVersionId(instance.RootDir, instance.MinecraftVersion, loaderVersion);
        var jsonPath = Path.Combine(instance.VersionsRoot, installedId, installedId + ".json");
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"Не найден установленный version.json: {jsonPath}");

        instance.VersionJsonId = installedId;
        instance.VersionUrl = "file://" + jsonPath;
        _metadata.ClearCache();

        var json = await _metadata.GetAsync(instance.VersionUrl, ct);
        json.VersionType = instance.VersionType;
        await _installer.InstallAsync(json, Target(instance), progress, includeClient: false, ct);

        File.Delete(installerPath);
        LogService.Default.Success($"{(neoForge ? "NeoForge" : "Forge")} {loaderVersion} установлен");
    }

    private static string FindInstalledVersionId(string rootDir, string mcVersion, string loaderVersion)
    {
        var versionsRoot = Path.Combine(rootDir, "versions");
        var exact = $"{mcVersion}-{loaderVersion}";
        if (Directory.Exists(Path.Combine(versionsRoot, exact))) return exact;

        var candidates = Directory.Exists(versionsRoot)
            ? Directory.GetDirectories(versionsRoot)
            : [];

        var match = candidates.FirstOrDefault(d =>
        {
            var name = Path.GetFileName(d);
            return name.StartsWith(mcVersion, StringComparison.OrdinalIgnoreCase) &&
                   name.Contains(loaderVersion.Split('-').Last());
        });

        return match is null ? exact : Path.GetFileName(match);
    }

    private static async Task<int> RunInstallerAsync(string javaPath, List<string> args, string workingDir,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить установщик");

        var outTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                LogService.Default.Trace("[установщик] " + line);
        });

        var errTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
                LogService.Default.Warn("[установщик] " + line);
        });

        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAny(outTask, errTask);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode;
    }

    private async Task<VersionJson> InstallProfileAsync(InstanceProfile instance, string url,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        instance.Isolated = true;
        instance.EnsureFolders();

        var childNode = await _metadata.GetNodeAsync(url, ct);
        var versionJson = await _metadata.MergeNodesAsync(childNode, FindVanillaUrlAsync,
            instance.VersionType, ct);

        var versionDir = Path.Combine(instance.VersionsRoot, versionJson.Id);
        AppPaths.Ensure(versionDir);
        var jsonPath = Path.Combine(versionDir, versionJson.Id + ".json");
        File.WriteAllText(jsonPath, Json.Serialize(versionJson));

        instance.VersionJsonId = versionJson.Id;
        instance.VersionUrl = "file://" + jsonPath;
        _metadata.ClearCache();

        await _installer.InstallAsync(versionJson, Target(instance), progress, ct: ct);
        return versionJson;
    }

    /// <summary>Ванильная версия из манифеста Mojang (родитель профиля загрузчика).</summary>
    private async Task<string?> FindVanillaUrlAsync(string versionId)
    {
        var manifest = await new MojangApi(_api).GetManifestAsync(false);
        return MojangApi.Find(manifest, versionId)?.Url;
    }

    private static InstallTarget Target(InstanceProfile instance) => InstallTarget.For(instance);
}