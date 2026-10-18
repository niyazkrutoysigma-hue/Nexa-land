using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>
/// Экспорт/импорт сборок в формате Modrinth .mrpack:
/// modrinth.index.json + список модов. Импорт ставит моды и обязательные зависимости.
/// </summary>
public sealed class ModpackService
{
    private readonly ModrinthService _modrinth;

    public ModpackService(ModrinthService modrinth) => _modrinth = modrinth;

    public sealed class ModEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
        [JsonPropertyName("env")] public JsonObject? Env { get; set; }
        [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    }

    public sealed class Index
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
        [JsonPropertyName("game")] public string Game { get; set; } = "minecraft";
        [JsonPropertyName("versionId")] public string VersionId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
        [JsonPropertyName("files")] public List<ModEntry> Files { get; set; } = [];
    }

    /// <summary>Метаданные мода, сохранённые лаунчером рядом с .jar (для экспорта и обновлений).</summary>
    public sealed class ModMarker
    {
        [JsonPropertyName("projectId")] public string ProjectId { get; set; } = string.Empty;
        [JsonPropertyName("versionId")] public string VersionId { get; set; } = string.Empty;
        [JsonPropertyName("source")] public string Source { get; set; } = "modrinth";
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("versionNumber")] public string VersionNumber { get; set; } = string.Empty;
        [JsonPropertyName("loadedTime")] public DateTime LoadedTime { get; set; }
    }

    private static string MarkerPath(string modsDir, string fileName) =>
        Path.Combine(modsDir, ".nexa", fileName + ".json");

    public void WriteMarker(string modsDir, ModMarker marker)
    {
        var path = MarkerPath(modsDir, marker.FileName);
        try
        {
            AppPaths.Ensure(Path.GetDirectoryName(path)!);
            Json.WriteFile(path, marker);
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Метаданные мода не сохранены: {ex.Message}");
        }
    }

    public ModMarker? ReadMarker(string modsDir, string fileName)
    {
        var path = MarkerPath(modsDir, fileName);
        return File.Exists(path) ? Json.ReadFile<ModMarker>(path) : null;
    }

    public IReadOnlyList<ModMarker> ReadAllMarkers(string modsDir)
    {
        var dir = Path.Combine(modsDir, ".nexa");
        if (!Directory.Exists(dir)) return [];

        var result = new List<ModMarker>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var marker = Json.ReadFile<ModMarker>(file);
            if (marker is not null) result.Add(marker);
        }

        return result;
    }

    // ---------------- Экспорт ----------------

    public async Task<string> ExportAsync(InstanceProfile instance, CancellationToken ct = default)
    {
        instance.EnsureFolders();

        var index = new Index
        {
            Name = instance.Name,
            VersionId = instance.MinecraftVersion,
            Summary = $"{instance.DisplayVersion} · NEXA LAND"
        };

        if (instance.Loader != LoaderType.None)
            index.Dependencies[instance.LoaderName.ToLowerInvariant()] = instance.LoaderVersion;

        foreach (var marker in ReadAllMarkers(instance.ModsDir))
        {
            if (string.IsNullOrEmpty(marker.ProjectId)) continue; // мод из файла — в пакет не включаем

            index.Files.Add(new ModEntry
            {
                Path = $"mods/{marker.FileName}",
                Downloads = [$"https://cdn.modrinth.com/data/{marker.ProjectId}/versions/{marker.VersionId}/{marker.FileName}"],
                Hashes = new Dictionary<string, string> { ["sha1"] = "0" }
            });
        }

        var packPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{Sanitize(instance.Name)}.mrpack");

        using (var stream = File.Create(packPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("modrinth.index.json");
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(Json.Serialize(index));
        }

        LogService.Default.Success($"Модпак собран: {packPath} ({index.Files.Count} модов)");
        return packPath;
    }

    // ---------------- Импорт ----------------

    public sealed class ImportResult
    {
        public int Installed { get; set; }
        public int Skipped { get; set; }
        public List<string> Failed { get; } = [];
    }

    public async Task<ImportResult> ImportAsync(string packPath, InstanceProfile instance,
        IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        if (!File.Exists(packPath)) throw new FileNotFoundException("Файл модпака не найден", packPath);

        Index? index;
        using (var archive = ZipFile.OpenRead(packPath))
        {
            var entry = archive.GetEntry("modrinth.index.json")
                        ?? throw new InvalidDataException("В модпаке нет modrinth.index.json");

            await using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            index = Json.Deserialize<Index>(await reader.ReadToEndAsync(ct))
                    ?? throw new InvalidDataException("Не удалось прочитать modrinth.index.json");
        }

        instance.EnsureFolders();
        var result = new ImportResult();
        var progressCounter = 0;

        foreach (var file in index.Files)
        {
            ct.ThrowIfCancellationRequested();
            progressCounter++;

            var fileName = Path.GetFileName(file.Path);
            var target = Path.Combine(instance.ModsDir, fileName);
            if (File.Exists(target))
            {
                result.Skipped++;
                continue;
            }

            if (file.Downloads.Count == 0)
            {
                result.Failed.Add(fileName);
                continue;
            }

            progress?.Report(new InstallProgress($"Импорт модпака: {progressCounter}/{index.Files.Count}",
                progressCounter * 100.0 / Math.Max(1, index.Files.Count), 0, 0, fileName));

            try
            {
                await AppServices.Api.DownloadFileAsync(file.Downloads[0], target, null, 0, null, ct, "Модпак");
                result.Installed++;
            }
            catch (Exception ex)
            {
                LogService.Default.Warn($"Мод из модпака не установлен ({fileName}): {ex.Message}");
                result.Failed.Add(fileName);
            }
        }

        LogService.Default.Success($"Модпак импортирован: +{result.Installed}, пропущено {result.Skipped}");
        return result;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}