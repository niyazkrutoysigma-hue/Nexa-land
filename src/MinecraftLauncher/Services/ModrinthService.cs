using System.Text;
using System.Web;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

public sealed class ModProject
{
    [System.Text.Json.Serialization.JsonPropertyName("project_id")] public string Id { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("downloads")] public long Downloads { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("follows")] public long Follows { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("loaders")] public List<string> Load { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("server_side")] public string? ServerSide { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("author")] public string? Author { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("date_modified")] public string? DateModified { get; set; }

    public string DownloadsText => $"{DownloadStatus.Format(Downloads)} загрузок";
    public bool IsClientOnly => ClientSide is "required" or "optional";
    public string ShortDescription => Description.Length > 90 ? Description[..90] + "…" : Description;
}

public sealed class ModFile
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("version_number")] public string VersionNumber { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("date_published")] public string? DatePublished { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("downloads")] public long Downloads { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [System.Text.Json.Serialization.JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("loaders")] public List<string> Load { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("dependencies")] public List<ModDependency> Dependencies { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("files")] public List<ModFileEntry> Files { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>В Modrinth name — заголовок версии; реальный файл лежит в files[].</summary>
    public ModFileEntry PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault() ?? new();

    /// <summary>Прямая ссылка на скачивание: url файла, иначе CDN-шаблон.</summary>
    public string DownloadUrl(string projectId)
    {
        if (!string.IsNullOrEmpty(Url)) return Url;

        var file = PrimaryFile;
        if (!string.IsNullOrEmpty(file.Url)) return file.Url;

        return $"https://cdn.modrinth.com/data/{projectId}/versions/{Id}/{Uri.EscapeDataString(Name)}";
    }

    public bool IsBeta => VersionType is "beta" or "alpha";
}

/// <summary>Файл внутри версии мода.</summary>
public sealed class ModFileEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("primary")] public bool Primary { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
}

public sealed class ModDependency
{
    [System.Text.Json.Serialization.JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("slug")] public string? Slug { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";
}

public sealed class ModFileInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("version_number")] public string VersionNumber { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("downloads")] public long Downloads { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [System.Text.Json.Serialization.JsonPropertyName("date_published")] public string? DatePublished { get; set; }
}

/// <summary>Каталог модов Modrinth: поиск, страницы, зависимости, обновления.</summary>
public sealed class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";

    private sealed class SearchResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("hits")] public List<ModProject> Hits { get; set; } = [];
        [System.Text.Json.Serialization.JsonPropertyName("total_hits")] public long TotalHits { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("offset")] public long Offset { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("limit")] public int Limit { get; set; }
    }

    private readonly ApiClient _api;

    public ModrinthService(ApiClient api) => _api = api;

    public sealed record Page(List<ModProject> Hits, long Total, long Offset);

    /// <summary>Страница результатов (до 100 за раз — ограничение Modrinth).</summary>
    public async Task<Page> SearchPageAsync(string query, string? gameVersion, string? loader,
        string? category, int offset, int limit = 100, string sort = "downloads",
        CancellationToken ct = default)
    {
        var facets = new List<string> { "project_type:mod" };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add($"versions:{gameVersion}");
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add($"categories:{loader}");
        if (!string.IsNullOrWhiteSpace(category)) facets.Add($"categories:{category}");

        var url = $"{Api}/search?query={HttpUtility.UrlEncode(query)}&limit={limit}&offset={offset}" +
                  $"&index={sort}&facets={HttpUtility.UrlEncode(EncodeFacets(facets))}";

        var response = await _api.GetJsonAsync<SearchResponse>(url, ct);
        return new Page(response?.Hits ?? [], response?.TotalHits ?? 0, response?.Offset ?? offset);
    }

    public async Task<List<ModProject>> SearchAsync(string query, string? gameVersion, string? loader,
        string? category, int limit = 40, CancellationToken ct = default)
    {
        var page = await SearchPageAsync(query, gameVersion, loader, category, 0, Math.Min(limit, 100), "newest", ct);
        return page.Hits;
    }

    public async Task<List<ModProject>> PopularAsync(string? gameVersion, string? loader,
        int limit = 24, CancellationToken ct = default)
    {
        var page = await SearchPageAsync("", gameVersion, loader, null, 0, Math.Min(limit, 100), "downloads", ct);
        return page.Hits;
    }

    /// <summary>Modrinth ждёт facets как массив массивов: [["a"],["b"]].</summary>
    private static string EncodeFacets(IEnumerable<string> facets) =>
        "[" + string.Join(",", facets.Select(f => $"[\"{f}\"]")) + "]";

    public async Task<List<ModFile>> GetFilesAsync(string projectId, CancellationToken ct = default)
    {
        var files = await _api.GetJsonAsync<List<ModFile>>($"{Api}/project/{projectId}/version", ct) ?? [];
        return files.OrderByDescending(f => f.DatePublished).ToList();
    }

    /// <summary>Совместимые с инстансом версии мода (по версии игры и загрузчику).</summary>
    public async Task<List<ModFile>> GetCompatibleFilesAsync(string projectId, string gameVersion,
        LoaderType loader, CancellationToken ct = default)
    {
        var loaderId = LoaderInfo.ModrinthId(loader);
        var files = await GetFilesAsync(projectId, ct);

        return files
            .Where(f => f.GameVersions.Contains(gameVersion))
            .Where(f => loaderId.Length == 0 || f.Load.Contains(loaderId))
            .OrderByDescending(f => f.VersionType == "release")
            .ThenByDescending(f => f.DatePublished)
            .ToList();
    }

    /// <summary>Последняя совместимая версия мода для сборки.</summary>
    public async Task<ModFileInfo?> GetLatestCompatibleAsync(string projectId, InstanceProfile instance,
        CancellationToken ct = default)
    {
        var files = await GetCompatibleFilesAsync(projectId, instance.MinecraftVersion, instance.Loader, ct);
        var best = files.FirstOrDefault();
        if (best is null) return null;

        return await GetFileInfoAsync(best.Id, ct);
    }

    public async Task<ModFileInfo?> GetFileInfoAsync(string versionId, CancellationToken ct = default)
    {
        try
        {
            var detailed = await _api.GetJsonAsync<ModFile>($"{Api}/version/{versionId}", ct);
            if (detailed is null) return null;

            var primary = detailed.PrimaryFile;
            var projectId = string.IsNullOrEmpty(detailed.ProjectId) ? "" : detailed.ProjectId;

            return new ModFileInfo
            {
                ProjectId = projectId,
                Id = versionId,
                // имя файла берём из files[], а не из name (name — заголовок версии)
                FileName = string.IsNullOrEmpty(primary.Filename) ? detailed.Name : primary.Filename,
                DownloadUrl = detailed.DownloadUrl(projectId),
                Size = primary.Size,
                Sha1 = primary.Hashes?.GetValueOrDefault("sha1"),
                VersionNumber = detailed.VersionNumber,
                Name = detailed.Name,
                Downloads = detailed.Downloads,
                VersionType = detailed.VersionType,
                DatePublished = detailed.DatePublished
            };
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Modrinth: не удалось получить {versionId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Устанавливает мод вместе с обязательными зависимостями (рекурсивно, с защитой от циклов).
    /// Возвращает список установленных файлов.
    /// </summary>
    public async Task<List<string>> InstallWithDependenciesAsync(ModFileInfo file, InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var installed = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await InstallRecursiveAsync(file, instance, installed, visited, progress, ct);
        return installed;
    }

    private async Task InstallRecursiveAsync(ModFileInfo file, InstanceProfile instance, List<string> installed,
        HashSet<string> visited, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!visited.Add(file.Id)) return;
        if (installed.Any(p => Path.GetFileName(p).Equals(file.FileName, StringComparison.OrdinalIgnoreCase)))
            return;

        var details = await _api.GetJsonAsync<ModFile>($"{Api}/version/{file.Id}", ct);
        var destination = Path.Combine(instance.ModsDir, SafeFileName(file.FileName));

        var projectId = string.IsNullOrEmpty(details?.ProjectId) ? file.ProjectId : details!.ProjectId;
        var downloadUrl = string.IsNullOrEmpty(file.DownloadUrl)
            ? details?.DownloadUrl(projectId)
            : file.DownloadUrl;

        if (string.IsNullOrEmpty(downloadUrl))
            downloadUrl = $"https://cdn.modrinth.com/data/{projectId}/versions/{file.Id}/{file.FileName}";

        progress?.Report(new InstallProgress($"Загрузка {file.Name}", 0, 0, 0, file.Name));
        await _api.DownloadFileAsync(downloadUrl, destination, file.Sha1, file.Size, null, ct, "Мод");
        installed.Add(destination);

        AppServices.Modpacks.WriteMarker(instance.ModsDir, new ModpackService.ModMarker
        {
            ProjectId = file.ProjectId,
            VersionId = file.Id,
            Source = "modrinth",
            FileName = Path.GetFileName(destination),
            Title = file.Name,
            VersionNumber = file.VersionNumber,
            LoadedTime = DateTime.Now
        });

        // обязательные зависимости
        foreach (var dependency in details?.Dependencies.Where(d => d.DependencyType == "required") ?? [])
        {
            if (dependency.VersionId is not { Length: > 0 } versionId) continue;

            var alreadyInstalled = installed.Any(p =>
                Path.GetFileNameWithoutExtension(p).Contains("fabric", StringComparison.OrdinalIgnoreCase) &&
                file.FileName.Contains(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase));

            if (alreadyInstalled) continue;

            var dependencyInfo = await GetFileInfoAsync(versionId, ct);
            if (dependencyInfo is null) continue;

            await InstallRecursiveAsync(dependencyInfo, instance, installed, visited, progress, ct);
        }
    }

    /// <summary>Скачивание мода по прямой ссылке (например, из буфера обмена).</summary>
    public async Task<string> InstallUrlAsync(string url, string fileName, InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        instance.EnsureFolders();
        var dest = Path.Combine(instance.ModsDir, SafeFileName(fileName));
        await _api.DownloadFileAsync(url, dest, null, 0, null, ct, "Мод");
        return dest;
    }

    /// <summary>Ищет обновления для установленных модов сборки.</summary>
    public async Task<List<ModUpdate>> CheckUpdatesAsync(InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var updates = new List<ModUpdate>();
        var markers = AppServices.Modpacks.ReadAllMarkers(instance.ModsDir)
            .Where(m => m.Source == "modrinth" && m.ProjectId.Length > 0)
            .ToList();

        if (markers.Count == 0) return updates;

        var done = 0;
        foreach (var marker in markers)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new InstallProgress("Проверка обновлений", done * 100.0 / markers.Count, 0, 0, marker.Title));

            try
            {
                var latest = await GetLatestCompatibleAsync(marker.ProjectId, instance, ct);
                if (latest is null || latest.Id == marker.VersionId) continue;

                updates.Add(new ModUpdate(marker, latest));
            }
            catch (Exception ex)
            {
                LogService.Default.Trace($"Обновление {marker.Title} не проверено: {ex.Message}");
            }
        }

        return updates;
    }

    public sealed record ModUpdate(ModpackService.ModMarker Installed, ModFileInfo Latest);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }

    /// <summary>Категории для чипов в UI (ru) + маппинг на Modrinth.</summary>
    public static IReadOnlyList<ModCategory> AllCategories { get; } =
    [
        new("", "Все", ""),
        new("optimization", "Оптимизация", "optimization"),
        new("horror", "Хоррор", "decoration"),
        new("decoration", "Декорации", "decoration"),
        new("technology", "Технологии", "technology"),
        new("gameplay", "Геймплей", "gameplay"),
        new("adventure", "Приключения", "adventure"),
        new("library", "Библиотеки", "library"),
        new("combat", "Бой", "combat"),
        new("movement", "Передвижение", "movement"),
        new("worldgen", "Генерация миров", "worldgen"),
        new("ui", "Интерфейс", "ui"),
        new("social", "Социальные", "social"),
        new("misc", "Разное", "")
    ];

    public sealed record ModCategory(string Key, string Title, string ModrinthSlug);
}