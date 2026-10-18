using System.Text.Json;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

public sealed class VersionManifest
{
    [System.Text.Json.Serialization.JsonPropertyName("latest")] public LatestVersions Latest { get; set; } = new();
    [System.Text.Json.Serialization.JsonPropertyName("versions")] public List<VersionSummary> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    [System.Text.Json.Serialization.JsonPropertyName("release")] public string Release { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("snapshot")] public string Snapshot { get; set; } = string.Empty;
}

/// <summary>Работа с piston-meta.mojang.com и прочими зеркалами Mojang.</summary>
public sealed class MojangApi
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    public const string LauncherMeta = "https://launchermeta.mojang.com/mc/game/launchermeta.json";
    public const string Resources = "https://resources.download.minecraft.net/";

    /// <summary>
    /// URL ассета по хешу. Актуальный формат — с префиксом из двух символов хеша
    /// (https://resources.download.minecraft.net/ab/abcdef...), у самых старых версий — без префикса.
    /// </summary>
    public static string AssetUrl(string hash, bool flat = false)
    {
        if (string.IsNullOrEmpty(hash)) return string.Empty;
        var prefix = flat || hash.Length < 3 ? string.Empty : hash[..2] + "/";
        return Resources + prefix + hash;
    }

    private readonly ApiClient _api;
    private string? _cachePath;
    private DateTime _cacheTime;

    public MojangApi(ApiClient api) => _api = api;

    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cache = Path.Combine(AppPaths.CacheDir, "version_manifest.json");

        if (!forceRefresh && File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromHours(3))
        {
            var cached = Json.ReadFile<VersionManifest>(cache);
            if (cached?.Versions.Count > 0)
            {
                _cachePath = cache;
                return cached;
            }
        }

        LogService.Default.Info("Обновляем список версий Minecraft с Mojang…");
        try
        {
            // качаем один раз: сырой JSON и для кэша, и для разбора
            var raw = await _api.GetStringAsync(ManifestUrl, ct);
            File.WriteAllText(cache, raw);

            var manifest = Json.Deserialize<VersionManifest>(raw)
                ?? throw new IOException("Пустой манифест версий");

            _cachePath = cache;
            _cacheTime = DateTime.Now;
            LogService.Default.Success($"Загружено версий: {manifest.Versions.Count}");
            return manifest;
        }
        catch (Exception ex)
        {
            if (File.Exists(cache))
            {
                LogService.Default.Warn($"Не удалось обновить манифест ({ex.Message}), используем кэш");
                var cached = Json.ReadFile<VersionManifest>(cache);
                if (cached is not null) return cached;
            }
            throw;
        }
    }

    /// <summary>Полный список релизов (без снапшотов), свежие сверху.</summary>
    public static IEnumerable<VersionSummary> Releases(VersionManifest manifest) =>
        manifest.Versions.Where(v => v.Type == "release").OrderByDescending(v => v.ReleaseTime);

    public static IEnumerable<VersionSummary> Snapshots(VersionManifest manifest) =>
        manifest.Versions.Where(v => v.Type == "snapshot").OrderByDescending(v => v.ReleaseTime);

    public static IEnumerable<VersionSummary> All(VersionManifest manifest) =>
        manifest.Versions.OrderByDescending(v => v.ReleaseTime);

    public static VersionSummary? Find(VersionManifest manifest, string id) =>
        manifest.Versions.FirstOrDefault(v => v.Id == id);
}