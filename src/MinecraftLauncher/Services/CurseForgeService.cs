using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Каталог CurseForge. Требует API-ключа (в настройках лаунчера).</summary>
public sealed class CurseForgeService
{
    private const string Api = "https://api.curseforge.com/v1";

    public sealed class Mod
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
        [JsonPropertyName("logo")] public Logo? Logo { get; set; }
        [JsonPropertyName("authors")] public List<Author> Authors { get; set; } = [];
        [JsonPropertyName("categories")] public List<Category> Categories { get; set; } = [];
        [JsonPropertyName("latestFiles")] public List<ModFile> LatestFiles { get; set; } = [];

        public string? ThumbnailUrl => Logo?.Url ?? Logo?.ThumbnailUrl;
        public string AuthorText => Authors.Count > 0 ? string.Join(", ", Authors.Take(2).Select(a => a.Name)) : "—";
        public string DownloadsText => DownloadStatus.Format(DownloadCount) + " загрузок";
        public string ModId => Id.ToString();
    }

    public sealed class Logo
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
    }

    public sealed class Author
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    public sealed class Category
    {
        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("id")] public int Id { get; set; }
    }

    public sealed class ModFile
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; } = [];
        [JsonPropertyName("fileLength")] public long FileLength { get; set; }
        [JsonPropertyName("fileDate")] public DateTime? FileDate { get; set; }
        [JsonPropertyName("dependencies")] public List<ModFileDependency> Dependencies { get; set; } = [];
    }

    public sealed class ModFileDependency
    {
        [JsonPropertyName("modId")] public long ModId { get; set; }
        [JsonPropertyName("relationType")] public int RelationType { get; set; }
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("data")] public List<Mod> Data { get; set; } = [];
    }

    private sealed class FilesResponse
    {
        [JsonPropertyName("data")] public List<ModFile> Data { get; set; } = [];
    }

    private readonly ApiClient _api;

    public CurseForgeService(ApiClient api) => _api = api;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SettingsService.Default.CurseForgeApiKey);

    public static readonly Dictionary<string, int> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["optimization"] = 6814,  // Performance
        ["horror"] = 10775,       // Horror
        ["decoration"] = 424,     // Cosmetic
        ["technology"] = 412,     // Technology
        ["gameplay"] = 5191,     // Utility & QoL
        ["adventure"] = 422,     // Adventure and RPG
        ["library"] = 421,       // API and Library
        ["combat"] = 434,        // Armor, Tools, and Weapons
        ["movement"] = 414,      // Player Transport
        ["worldgen"] = 406,      // World Gen
        ["ui"] = 423,            // Map and Information
        ["social"] = 435,        // Server Utility
        ["misc"] = 425           // Miscellaneous
    };

    public static readonly Dictionary<LoaderType, int> LoaderMap = new()
    {
        [LoaderType.Fabric] = 4,   // Fabric
        [LoaderType.Quilt] = 5,    // Quilt
        [LoaderType.Forge] = 1,    // Forge
        [LoaderType.NeoForge] = 6, // NeoForge
        [LoaderType.None] = 0      // Any
    };

    /// <summary>Поиск модов через GET /v1/mods/search (стандартный endpoint CurseForge).</summary>
    public async Task<List<Mod>> SearchAsync(string query, string? gameVersion, string? loader,
        int? categoryId, int index, int pageSize, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        var loaderType = loader switch
        {
            "fabric" => 4,
            "quilt" => 5,
            "forge" => 1,
            "neoforge" => 6,
            _ => 0
        };

        var url = $"{Api}/mods/search?gameId=432&classId=6&pageSize={pageSize}&index={index}" +
                  $"&sortField=2&sortOrder=desc";

        if (!string.IsNullOrWhiteSpace(query))
            url += $"&searchFilter={HttpUtility.UrlEncode(query)}";

        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"&gameVersion={HttpUtility.UrlEncode(gameVersion)}";

        if (loaderType > 0)
            url += $"&modLoaderType={loaderType}";

        if (categoryId is > 0)
            url += $"&categoryId={categoryId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", SettingsService.Default.CurseForgeApiKey);

        using var response = await _api.Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new IOException($"CurseForge: {(int)response.StatusCode} {ExtractError(text)}");

        return Json.Deserialize<SearchResponse>(text)?.Data ?? [];
    }

    public async Task<List<ModFile>> GetFilesAsync(long modId, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/mods/{modId}/files");
        request.Headers.Add("x-api-key", SettingsService.Default.CurseForgeApiKey);

        using var response = await _api.Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"CurseForge: {ExtractError(text)}");

        return Json.Deserialize<FilesResponse>(text)?.Data ?? [];
    }

    public async Task<ModFile?> GetCompatibleFileAsync(long modId, string gameVersion, LoaderType loader,
        CancellationToken ct = default)
    {
        var files = await GetFilesAsync(modId, ct);
        var loaderName = loader switch
        {
            LoaderType.Fabric => "Fabric",
            LoaderType.Quilt => "Quilt",
            LoaderType.Forge => "Forge",
            LoaderType.NeoForge => "NeoForge",
            _ => ""
        };

        return files
            .Where(f => f.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            .Where(f => string.IsNullOrEmpty(loaderName) || f.GameVersions.Contains(loaderName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.FileDate)
            .FirstOrDefault();
    }

    public async Task<string> InstallAsync(ModFile file, InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl)) throw new IOException("У файла нет ссылки на скачивание");

        instance.EnsureFolders();
        var dest = Path.Combine(instance.ModsDir, file.FileName);
        await _api.DownloadFileAsync(file.DownloadUrl, dest, null, file.FileLength, null, ct, "Мод CurseForge");
        return dest;
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? json;
            if (doc.RootElement.TryGetProperty("errorMessage", out var errorMessage))
                return errorMessage.GetString() ?? json;
        }
        catch
        {
            // не JSON — вернём как есть
        }

        return json.Length > 200 ? json[..200] : json;
    }
}