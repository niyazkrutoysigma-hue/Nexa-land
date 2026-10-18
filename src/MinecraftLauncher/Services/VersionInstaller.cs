using System.IO.Compression;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Общий прогресс установки.</summary>
public sealed record InstallProgress(string Phase, double Percent, long Received, long Total, string? CurrentFile = null)
{
    public string Text => Total > 0
        ? $"{Phase} — {DownloadStatus.Format(Received)} / {DownloadStatus.Format(Total)} ({Percent:0}%)"
        : $"{Phase}{(string.IsNullOrEmpty(CurrentFile) ? "" : " — " + CurrentFile)}";
}

/// <summary>Куда ставить файлы конкретного инстанса.</summary>
public sealed class InstallTarget
{
    public required string VersionsRoot { get; init; }
    public required string LibraryRoot { get; init; }
    public required string AssetsRoot { get; init; }

    /// <summary>Куда класть клиентский jar (у загрузчиков — в папку ванильной версии).</summary>
    public required string ClientJarPath { get; init; }

    public string VersionDir(string id) => Path.Combine(VersionsRoot, id);

    public string AssetIndexFile(string id) => Path.Combine(AssetsRoot, "indexes", id + ".json");

    public static InstallTarget For(InstanceProfile instance) => new()
    {
        VersionsRoot = instance.VersionsRoot,
        LibraryRoot = instance.LibraryRoot,
        AssetsRoot = instance.AssetsRoot,
        ClientJarPath = instance.ClientJarPath
    };
}

/// <summary>Скачивание клиента, библиотек, ассетов и распаковка natives.</summary>
public sealed class VersionInstaller
{
    private readonly ApiClient _api;
    private readonly VersionMetadataService _metadata;

    public VersionInstaller(ApiClient api, VersionMetadataService metadata)
    {
        _api = api;
        _metadata = metadata;
    }

    private sealed record Work(string Url, string Dest, string? Sha1, long Size);

    /// <summary>Читает objects из индекса ассетов (формат: {"objects": {...}}).</summary>
    private static Dictionary<string, AssetObject> ReadAssetIndex(string path)
    {
        try
        {
            var index = Json.Deserialize<AssetIndexFile>(File.ReadAllText(path));
            return index?.Objects ?? new Dictionary<string, AssetObject>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Индекс ассетов не прочитан: {ex.Message}");
            return new Dictionary<string, AssetObject>(StringComparer.Ordinal);
        }
    }

    private sealed class AssetObject
    {
        [System.Text.Json.Serialization.JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>Файл индекса ассетов: {"objects": {"minecraft/...": {hash, size}}}.</summary>
    private sealed class AssetIndexFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("objects")]
        public Dictionary<string, AssetObject> Objects { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Plan(List<Work> files, List<LibraryDef> natives, Dictionary<string, AssetObject> assets)
    {
        public List<Work> Files { get; } = files;
        public List<LibraryDef> Natives { get; } = natives;
        public Dictionary<string, AssetObject> Assets { get; set; } = assets;
    }

    /// <summary>План загрузки: какие файлы нужны и куда класть natives.</summary>
    private Plan BuildPlan(VersionJson version, InstallTarget target, bool includeClient)
    {
        var work = new List<Work>();
        var natives = new List<LibraryDef>();
        var state = RuleState.Default;

        // 1. Клиентский jar
        if (includeClient && version.Downloads?.Client?.Url is { } clientUrl)
        {
            work.Add(new Work(clientUrl, target.ClientJarPath, version.Downloads.Client.Sha1,
                version.Downloads.Client.Size));
        }

        // 2. Библиотеки (с учётом правил ОС)
        foreach (var lib in version.Libraries ?? [])
        {
            if (lib.Rules.Count > 0 && !Evaluate(lib.Rules, state)) continue;

            var artifact = lib.GetArtifact(state);
            var mavenPath = !string.IsNullOrEmpty(lib.Name)
                ? lib.MavenPath
                : artifact?.Path ?? string.Empty;

            // Базовый maven-репозиторий: profile.json загрузчиков не содержит блока downloads
            var url = artifact?.Url
                      ?? (lib.Url is { Length: > 0 } repo && mavenPath.Length > 0
                          ? repo.TrimEnd('/') + "/" + mavenPath
                          : null);
            if (url is null || mavenPath.Length == 0) continue;

            if (lib.IsNative)
            {
                // natives скачиваем отдельно: в classpath они не попадают
                if (lib.Downloads?.Classifiers is { } classifiers &&
                    lib.NativeClassifier is { } classifier &&
                    classifiers.TryGetValue(classifier, out var nativeArtifact) && nativeArtifact.Url is not null)
                {
                    natives.Add(lib);
                    work.Add(new Work(nativeArtifact.Url,
                        Path.Combine(AppPaths.TempDir, "natives", SafeTempName(lib.NameParts)),
                        nativeArtifact.Sha1, nativeArtifact.Size));
                }

                // В версиях до 1.17 natives описан в том же элементе, что и обычный jar:
                // сам jar тоже обязателен, иначе в classpath не будет, например, lwjgl-glfw.
                if (lib.Downloads?.Artifact is { Url: { Length: > 0 } plainUrl } plain)
                {
                    work.Add(new Work(plainUrl,
                        Path.Combine(target.LibraryRoot, mavenPath.Replace('/', Path.DirectorySeparatorChar)),
                        plain.Sha1, plain.Size));
                }

                continue;
            }

            work.Add(new Work(url, Path.Combine(target.LibraryRoot, mavenPath.Replace('/', Path.DirectorySeparatorChar)),
                artifact?.Sha1, artifact?.Size ?? 0));
        }

        // 3. Логгер клиента (log4j для старых версий)
        if (version.ClientLogging?.File?.Url is { } logUrl)
        {
            work.Add(new Work(logUrl,
                Path.Combine(target.VersionDir(version.Id), "logging", Path.GetFileName(logUrl)),
                version.ClientLogging.File.Sha1, version.ClientLogging.File.Size));
        }

        // 4. Ассеты
        var assets = new Dictionary<string, AssetObject>(StringComparer.Ordinal);
        if (version.AssetIndex?.Url is { } assetUrl)
        {
            var indexPath = target.AssetIndexFile(version.AssetIndex.Id ?? version.Id);
            if (File.Exists(indexPath))
            {
                assets = ReadAssetIndex(indexPath);
            }
        }

        return new Plan(work, natives, assets);
    }

    private static string SafeTempName(string[] nameParts)
    {
        var file = nameParts.Length >= 2 ? $"{nameParts[1]}.jar" : $"{nameParts.GetHashCode()}.jar";
        foreach (var c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return file;
    }

    /// <summary>Быстрая проверка комплектности — без обращения к сети.</summary>
    public bool IsInstalled(VersionJson version, InstallTarget target)
    {
        var includeClient = version.Downloads?.Client?.Url is not null;
        var plan = BuildPlan(version, target, includeClient);

        foreach (var file in plan.Files)
        {
            if (!File.Exists(file.Dest)) return false;
        }

        // Ассеты: достаточно наличия индекса и его объектов
        if (plan.Assets.Count > 0)
        {
            foreach (var asset in plan.Assets.Values)
            {
                if (!File.Exists(AssetPath(target.AssetsRoot, asset.Hash))) return false;
            }
        }
        else if (version.AssetIndex?.Url is not null)
        {
            return false; // индекс ещё не скачан
        }

        // natives должны быть распакованы
        if (plan.Natives.Count > 0)
        {
            var nativesDir = Path.Combine(target.VersionDir(version.Id), "natives");
            if (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir).Any()) return false;
        }

        return true;
    }

    public async Task InstallAsync(VersionJson version, InstallTarget target, IProgress<InstallProgress>? progress,
        bool includeClient = true, CancellationToken ct = default)
    {
        var plan = BuildPlan(version, target, includeClient);

        var assetUrl = version.AssetIndex?.Url;
        string? assetIndexId = version.AssetIndex?.Id ?? version.Id;
        var indexPath = target.AssetIndexFile(assetIndexId);

        // 1. Индекс ассетов
        if (assetUrl is not null)
        {
            progress?.Report(new InstallProgress("Индекс ассетов", 0, 0, 0));
            await _api.DownloadFileAsync(assetUrl, indexPath, version.AssetIndex?.Sha1,
                version.AssetIndex?.Size ?? 0, null, ct, "Индекс ассетов");

            if (plan.Assets.Count == 0 && File.Exists(indexPath))
            {
                plan.Assets = ReadAssetIndex(indexPath);
            }
        }

        // 2. Считаем общий объём для нормального прогресса
        var missingAssets = plan.Assets.Values
            .Where(a => !File.Exists(AssetPath(target.AssetsRoot, a.Hash)))
            .Select(a => new Work(MojangApi.AssetUrl(a.Hash, UsesFlatAssetUrls(version)),
                AssetPath(target.AssetsRoot, a.Hash), a.Hash, a.Size))
            .ToList();

        var missingFiles = plan.Files.Where(f => !Exists(f)).ToList();
        var totalBytes = missingFiles.Sum(f => f.Size) + missingAssets.Sum(f => f.Size);

        if (missingAssets.Count > 0)
        {
            await DownloadManyAsync(missingAssets, progress, totalBytes, ct, $"Ассеты ({missingAssets.Count})", 8);
        }

        if (missingFiles.Count > 0)
        {
            await DownloadManyAsync(missingFiles, progress, totalBytes, ct,
                $"Файлы версии ({missingFiles.Count})", 6);
        }

        // 3. Распаковка natives
        if (plan.Natives.Count > 0)
        {
            var nativesDir = Path.Combine(target.VersionDir(version.Id), "natives");
            progress?.Report(new InstallProgress("Распаковка нативных библиотек", 98, totalBytes, totalBytes));
            ExtractNatives(plan.Natives, nativesDir);
        }

        progress?.Report(new InstallProgress($"{version.Id} готова", 100, totalBytes, totalBytes));
        LogService.Default.Success($"Файлы версии {version.Id} на месте");
    }

    private static bool Exists(Work work) =>
        File.Exists(work.Dest) && (work.Size <= 0 || new FileInfo(work.Dest).Length == work.Size);

    private async Task DownloadManyAsync(List<Work> list, IProgress<InstallProgress>? progress,
        long totalBytes, CancellationToken ct, string phase, int concurrency)
    {
        if (list.Count == 0) return;

        long done = 0;
        var gate = new object();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Parallel.ForEachAsync(list, new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct
        }, async (item, token) =>
        {
            if (!Exists(item))
            {
                await _api.DownloadFileAsync(item.Url, item.Dest, item.Sha1, item.Size, null, token, phase);
            }

            lock (gate)
            {
                done += item.Size;
                var percent = totalBytes > 0 ? Math.Min(99, done * 100.0 / totalBytes) : 0;
                progress?.Report(new InstallProgress(phase, percent, done, totalBytes,
                    Path.GetFileName(item.Dest)));
            }
        });

        LogService.Default.Info($"{phase}: {list.Count} файлов за {sw.Elapsed.TotalSeconds:0.#}с");
    }

    private void ExtractNatives(List<LibraryDef> libs, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        var tempRoot = Path.Combine(AppPaths.TempDir, "natives");

        foreach (var lib in libs)
        {
            var path = Path.Combine(tempRoot, SafeTempName(lib.NameParts));
            if (!File.Exists(path)) continue;

            var exclude = lib.Extract?.Exclude ?? [];
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;

                var relative = entry.FullName.Replace('\\', '/');
                var excluded = exclude.Any(ex => MatchesExclude(relative, ex));
                if (excluded) continue;

                var destination = Path.Combine(nativesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        LogService.Default.Trace($"Natives распакованы в {nativesDir}");
    }

    private static bool MatchesExclude(string relative, string pattern)
    {
        var normalized = pattern.Replace("**", "*").Replace('\\', '/').TrimStart('/');
        if (relative.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.StartsWith('*') && relative.EndsWith(normalized[1..], StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.EndsWith('*') && relative.StartsWith(normalized[..^1], StringComparison.OrdinalIgnoreCase))
            return true;
        return relative.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string AssetPath(string assetsRoot, string hash) =>
        Path.Combine(assetsRoot, "objects", hash.Length >= 2 ? hash[..2] : "00", hash);

    /// <summary>У версий до 1.5 ассеты лежат без двухсимвольного префикса в URL.</summary>
    private static bool UsesFlatAssetUrls(VersionJson version)
    {
        var core = (version.AssetIndex?.Id ?? version.Id).Split('-')[0];
        if (Version.TryParse(core, out var parsed))
        {
            return parsed.Major == 1 && parsed.Minor <= 5;
        }

        // alpha/beta/rc версии
        return !char.IsDigit(version.Id.Length > 0 ? version.Id[0] : '1');
    }

    private static bool Evaluate(List<Rule> rules, RuleState state)
    {
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(state)) allowed = rule.Action == "allow";
        }
        return allowed;
    }
}