using System.IO.Compression;
using System.Text.Json.Serialization;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>Скачивание Eclipse Temurin (Adoptium) JRE нужной версии.</summary>
public sealed class AdoptiumService
{
    private const string Api = "https://api.adoptium.net/v3";

    public sealed class Asset
    {
        [JsonPropertyName("binary")] public Binary Binary { get; set; } = new();
    }

    public sealed class Binary
    {
        [JsonPropertyName("package")] public Package Package { get; set; } = new();
        [JsonPropertyName("image_type")] public string ImageType { get; set; } = "jdk";
    }

    public sealed class Package
    {
        [JsonPropertyName("link")] public string Link { get; set; } = string.Empty;
        [JsonPropertyName("checksum")] public string Checksum { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    public sealed class Release
    {
        [JsonPropertyName("release_name")] public string ReleaseName { get; set; } = string.Empty;
        [JsonPropertyName("bin")] public List<Binary> Bin { get; set; } = [];
    }

    private readonly ApiClient _api;

    public AdoptiumService(ApiClient api) => _api = api;

    public async Task<(string Url, string Name, long Size)?> GetLatestJreUrlAsync(int major,
        CancellationToken ct = default)
    {
        // Актуальная форма запроса Adoptium v3 — с параметрами. Старый путь
        // /assets/latest/{major}/ga/windows/x64/... отдаёт 404, в том числе для Java 8.
        var url = $"{Api}/assets/latest/{major}/hotspot" +
                  "?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os=windows&vendor=eclipse";
        LogService.Default.Info($"Ищем Temurin {major} (x64)…");

        var assets = await _api.GetJsonAsync<List<Asset>>(url, ct);
        var package = assets?.FirstOrDefault()?.Binary?.Package;
        if (package is null || string.IsNullOrEmpty(package.Link)) return null;

        return (package.Link, package.Name, package.Size);
    }

    public async Task<JavaLocator.JavaInfo?> DownloadJavaAsync(int major, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var info = await GetLatestJreUrlAsync(major, ct);
        if (info is null)
        {
            LogService.Default.Error($"Temurin {major} не найден. Укажите путь к Java вручную в настройках.");
            return null;
        }

        var (url, name, size) = info.Value;
        LogService.Default.Success($"Скачиваем Temurin {major}: {name}");

        var folder = Path.Combine(AppPaths.JavaRoot, name.Replace(".zip", "").Replace(".tar.gz", ""));
        var javaHome = Path.Combine(folder, name.Contains("jdk-")
            ? new DirectoryInfo(FindRootInZipName(name)).Name
            : "jdk-" + major + (name.Contains("+") ? "+" + name.Split('+')[1] : ""));

        if (File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
        {
            LogService.Default.Info("Java уже скачана");
            return JavaLocator.Probe(javaHome);
        }

        var zipPath = Path.Combine(AppPaths.DownloadsDir, name);
        await _api.DownloadFileAsync(url, zipPath, null, size, null, ct, "Java");

        progress?.Report(new InstallProgress("Распаковка Java", 99, 0, 0, name));
        Directory.CreateDirectory(folder);

        // Внутри zip лежит единственная папка вида jdk-21.0.2+13
        var root = FindSingleRootFolder(zipPath);
        ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);

        if (root is not null && Directory.Exists(Path.Combine(folder, root)))
        {
            javaHome = Path.Combine(folder, root);
        }

        File.Delete(zipPath);

        var result = JavaLocator.Probe(javaHome);
        if (result is null) LogService.Default.Error($"Java распакована, но java.exe не найден в {javaHome}");
        else LogService.Default.Success($"Java готова: {result.Name}");
        return result;
    }

    private static string FindRootInZipName(string name) => name.Split('-').Length > 1 ? name : name;

    private static string? FindSingleRootFolder(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var root = archive.Entries
                .Select(e => e.FullName.Split('/')[0])
                .Distinct()
                .ToList();
            return root.Count == 1 ? root[0] : null;
        }
        catch
        {
            return null;
        }
    }
}