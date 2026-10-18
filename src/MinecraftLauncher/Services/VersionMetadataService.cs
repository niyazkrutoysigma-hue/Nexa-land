using System.Text.Json;
using System.Text.Json.Nodes;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>
/// Загрузка и слияние version.json с учётом цепочки inheritsFrom.
/// Слияние делается на уровне JSON-дерева: библиотеки объединяются (так требуют профили
/// Fabric/Quilt, наследующие ванильные библиотеки), остальные поля ребёнка перекрывают родителя.
/// </summary>
public sealed class VersionMetadataService
{
    private readonly ApiClient _api;
    private readonly Dictionary<string, VersionJson> _cache = new(StringComparer.Ordinal);

    public VersionMetadataService(ApiClient api) => _api = api;

    public async Task<VersionJson> GetAsync(string url, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(url, out var cached)) return cached;

        var raw = await _api.GetStringAsync(url, ct);
        var json = JsonNode.Parse(raw) as JsonObject
                   ?? throw new IOException($"Некорректный version.json: {url}");

        var result = json.Deserialize<VersionJson>(Json.Options)
                     ?? throw new IOException($"Не удалось прочитать version.json: {url}");

        _cache[url] = result;
        return result;
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Универсальное разрешение наследования: если в версии есть inheritsFrom,
    /// подтягивается родитель (по локальному стору или через переданный делегат поиска URL).
    /// </summary>
    public async Task<VersionJson> MergeNodesAsync(JsonNode childNode, Func<string, Task<string?>> findParentUrl,
        string versionType, CancellationToken ct = default)
    {
        JsonNode? parentNode = null;

        if (childNode["inheritsFrom"]?.GetValue<string>() is { Length: > 0 } parentId)
        {
            // 1. Локальный стор (ванильные версии и файлы, установленные загрузчиками)
            foreach (var candidate in LocalVersionCandidates(parentId))
            {
                if (!File.Exists(candidate)) continue;
                parentNode = JsonNode.Parse(await File.ReadAllTextAsync(candidate, ct));
                LogService.Default.Trace($"Наследование: {parentId} из локального стора");
                break;
            }

            // 2. Через внешний источник (манифест Mojang)
            if (parentNode is null)
            {
                var url = await findParentUrl(parentId);
                if (url is not null) parentNode = await GetNodeAsync(url, ct);
            }
        }

        return MergeNodes(parentNode, childNode, versionType);
    }

    private static IEnumerable<string> LocalVersionCandidates(string parentId)
    {
        yield return Path.Combine(AppPaths.SharedVersions, parentId, parentId + ".json");

        var instancesFolder = SettingsService.Default.EffectiveInstancesFolder;
        if (!Directory.Exists(instancesFolder)) yield break;

        foreach (var instanceDir in Directory.GetDirectories(instancesFolder))
        {
            yield return Path.Combine(instanceDir, "versions", parentId, parentId + ".json");
        }
    }

    /// <summary>Загружает сырой JSON-узел версии (для слияния без потери полей).</summary>
    public async Task<JsonNode> GetNodeAsync(string url, CancellationToken ct = default)
    {
        var raw = await _api.GetStringAsync(url, ct);
        return JsonNode.Parse(raw) ?? throw new IOException($"Пустой version.json: {url}");
    }

    /// <summary>
    /// Сливает дочерний version.json с родительским и возвращает готовый объект.
    /// Результат не содержит inheritsFrom — наследование уже разрешено.
    /// </summary>
    public VersionJson MergeNodes(JsonNode? parentNode, JsonNode? childNode, string versionType)
    {
        var merged = MergeRaw(parentNode, childNode);
        var result = merged.Deserialize<VersionJson>(Json.Options) ?? new VersionJson();
        result.VersionType = versionType;
        merged.Remove("inheritsFrom");
        return result;
    }

    public static JsonObject MergeRaw(JsonNode? parentNode, JsonNode? childNode)
    {
        var result = parentNode?.DeepClone() as JsonObject ?? new JsonObject();

        if (childNode is not JsonObject child) return result;

        foreach (var (key, value) in child.ToList())
        {
            if (value is null) continue;

            switch (key)
            {
                case "libraries":
                    result["libraries"] = MergeLibraries(result["libraries"] as JsonArray, value as JsonArray);
                    break;

                case "arguments":
                    result["arguments"] = MergeArguments(result["arguments"] as JsonObject, value as JsonObject);
                    break;

                default:
                    result[key] = value.DeepClone();
                    break;
            }
        }

        return result;
    }

    /// <summary>Библиотеки родителя идут первыми, библиотеки ребёнка добавляются/заменяют дубли.</summary>
    private static JsonArray MergeLibraries(JsonArray? parent, JsonArray? child)
    {
        var result = new JsonArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddRange(JsonArray? source)
        {
            if (source is null) return;
            foreach (var node in source)
            {
                if (node is not JsonObject obj) continue;
                var key = LibraryKey(obj);
                if (index.TryGetValue(key, out var position))
                {
                    result[position] = node.DeepClone(); // ребёнок приоритетнее
                }
                else
                {
                    index[key] = result.Count;
                    result.Add(node.DeepClone());
                }
            }
        }

        AddRange(parent);
        AddRange(child);
        return result;
    }

    /// <summary>
    /// Ключ дедупликации библиотек. Важно: в версиях до 1.17 Mojang описывает каждую
    /// LWJGL-библиотеку двумя элементами массива — обычный jar и natives-классификатор, —
    /// причём <c>downloads.artifact.path</c> у них ОДИНАКОВЫЙ. Если не различать их по
    /// наличию блока <c>natives</c>, вторая запись затирает первую, обычный jar не попадает
    /// ни в план установки, ни в classpath, и игра падает с
    /// <c>NoClassDefFoundError: org/lwjgl/glfw/GLFW</c>.
    /// </summary>
    private static string LibraryKey(JsonObject library)
    {
        var kind = library["natives"] is JsonObject natives && natives.Count > 0 ? "natives" : "main";

        if (library["downloads"] is JsonObject downloads &&
            downloads["artifact"] is JsonObject artifact &&
            artifact["path"]?.GetValue<string>() is { Length: > 0 } path)
        {
            return kind + "|" + path;
        }

        return kind + "|" + (library["name"]?.GetValue<string>() ?? library.ToJsonString());
    }

    /// <summary>Секции game/jvm дополняются: недостающие берутся у родителя.</summary>
    private static JsonObject? MergeArguments(JsonObject? parent, JsonObject? child)
    {
        if (parent is null) return child?.DeepClone() as JsonObject;
        if (child is null) return parent.DeepClone() as JsonObject;

        var result = parent.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var (key, value) in child)
        {
            if (key is "game" or "jvm")
            {
                result[key] = MergeArgumentSection(result[key] as JsonArray, value as JsonArray);
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    private static JsonArray MergeArgumentSection(JsonArray? parent, JsonArray? child)
    {
        if (parent is null || parent.Count == 0) return child?.DeepClone() as JsonArray ?? [];
        if (child is null || child.Count == 0) return parent.DeepClone() as JsonArray ?? [];

        // Аргументы ребёнка заменяют родительские (ванильное поведение),
        // но элементы с одинаковым набором правил дополняются по значению.
        var parentByKey = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var node in parent)
        {
            if (node is JsonObject obj && obj["rules"] is JsonArray rules)
                parentByKey[rules.ToJsonString()] = node.DeepClone();
        }

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in child)
        {
            if (node is not null) result.Add(node.DeepClone());
            if (node is JsonObject obj && obj["rules"] is JsonArray rules)
                seen.Add(rules.ToJsonString());
        }

        foreach (var (key, node) in parentByKey)
        {
            if (seen.Contains(key)) continue;
            result.Add(node);
        }

        return result;
    }
}