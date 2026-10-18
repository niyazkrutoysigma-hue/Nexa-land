using System.Text;
using System.Text.Json.Nodes;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>
/// Диагностика без запуска игры: проверяет сеть, разбор version.json, наследование,
/// правила библиотек, подбор Java и сборку аргументов запуска.
/// Запуск: MinecraftLauncher.exe --selftest [версия, например 1.21.1]
/// </summary>
public sealed class SelfTestService
{
    private readonly StringBuilder _report = new();

    public string BuildReport => _report.ToString();

    private async Task<bool> CheckAsync(string name, Func<Task<string>> action)
    {
        try
        {
            var detail = await action();
            _report.AppendLine($"[OK]   {name}: {detail}");
            LogService.Default.Success($"{name}: {detail}");
            return true;
        }
        catch (Exception ex)
        {
            _report.AppendLine($"[FAIL] {name}: {ex.Message}");
            LogService.Default.Error($"{name}: {ex.Message}", ex);
            return false;
        }
    }

    public async Task<bool> RunAsync(string? versionFilter, CancellationToken ct = default)
    {
        _report.AppendLine($"{NexaBrand.Title} — самопроверка {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _report.AppendLine(new string('-', 64));

        var passed = 0;
        var total = 0;

        async Task<bool> Run(string name, Func<Task<string>> action)
        {
            total++;
            if (await CheckAsync(name, action)) passed++;
            return true;
        }

        // 1. Java
        await Run("Java на машине", () =>
        {
            AppServices.Java.Refresh();
            var found = AppServices.Java.Found;
            if (found.Count == 0) throw new InvalidOperationException("не найдена (будет скачана автоматически)");
            return Task.FromResult(string.Join("; ", found.Take(4).Select(j => $"{j.Vendor} {j.Version}")));
        });

        // 2. Манифест версий
        VersionManifest? manifest = null;
        await Run("Манифест Mojang", async () =>
        {
            manifest = await AppServices.Mojang.GetManifestAsync(true, ct);
            return $"версий: {manifest.Versions.Count}, релиз: {manifest.Latest.Release}";
        });

        if (manifest is null)
        {
            _report.AppendLine("Дальнейшие проверки невозможны: нет манифеста.");
            return passed == total;
        }

        var summary = versionFilter is { Length: > 0 }
            ? MojangApi.Find(manifest, versionFilter)
            : MojangApi.Releases(manifest).FirstOrDefault();

        if (summary is null)
        {
            _report.AppendLine($"Версия {versionFilter} не найдена в манифесте.");
            return passed == total;
        }

        Task<string?> FindParentUrl(string id) =>
            Task.FromResult<string?>(MojangApi.Find(manifest!, id)?.Url ?? "");

        // 3. Разбор version.json
        await Run($"Version.json {summary.Id}", async () =>
        {
            var node = await AppServices.Metadata.GetNodeAsync(summary.Url, ct);
            var parsed = node.Deserialize<VersionJson>(Json.Options) ?? new VersionJson();
            var java = parsed.JavaMajor > 0 ? parsed.JavaMajor : JavaRequirements.For(summary.Id);
            return $"mainClass={parsed.MainClass}, библиотек={parsed.Libraries?.Count ?? 0}, Java={java}";
        });

        // 4. Аргументы запуска в version.json
        await Run("Парсинг аргументов", async () =>
        {
            var node = await AppServices.Metadata.GetNodeAsync(summary.Url, ct);
            var parsed = node.Deserialize<VersionJson>(Json.Options) ?? new VersionJson();

            if (parsed.MinecraftArguments is not null)
            {
                return $"старый формат, minecraftArguments: {parsed.MinecraftArguments.Length} символов";
            }

            var args = ArgumentParser.Parse(parsed.Arguments);
            var game = args.Game.Sum(a => a.Value.Count);
            var jvm = args.Jvm.Sum(a => a.Value.Count);
            if (game == 0 && jvm == 0) throw new InvalidOperationException("аргументы не найдены");
            return $"game: {game}, jvm: {jvm}";
        });

        // 5. Правила ОС и нативные библиотеки
        await Run("Правила библиотек (rules/natives)", async () =>
        {
            var node = await AppServices.Metadata.GetNodeAsync(summary.Url, ct);
            var parsed = node.Deserialize<VersionJson>(Json.Options) ?? new VersionJson();

            var state = RuleState.Default;
            var all = parsed.Libraries ?? [];
            var allowed = all.Count(l => Evaluate(l.Rules, state));
            var natives = all.Count(l => l.IsNative && l.NativeClassifier is not null);

            if (allowed == 0 && all.Count > 0)
                throw new InvalidOperationException("ни одна библиотека не прошла правила для Windows");

            return $"подходит для Windows: {allowed} из {all.Count}, нативных: {natives}";
        });

        // 6. Слияние профиля загрузчика с ванильной версией
        await Run("Слияние профиля Fabric", async () =>
        {
            const string loaderVersion = "0.16.9";
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{summary.Id}/{loaderVersion}/profile/json";
            var child = await AppServices.Metadata.GetNodeAsync(url, ct);

            var parentId = child["inheritsFrom"]?.GetValue<string>();
            if (parentId is null) throw new InvalidOperationException("у профиля нет inheritsFrom");

            var parentUrl = await FindParentUrl(parentId);
            if (parentUrl is null || parentUrl.Length == 0)
                throw new FileNotFoundException($"нет ванильной версии {parentId}");

            var parent = await AppServices.Metadata.GetNodeAsync(parentUrl, ct);
            var merged = VersionMetadataService.MergeRaw(parent, child)
                .Deserialize<VersionJson>(Json.Options) ?? new VersionJson();

            var libs = merged.Libraries ?? [];
            var fabric = libs.Count(l => l.Name?.StartsWith("net.fabricmc", StringComparison.OrdinalIgnoreCase) == true);
            var lwjgl = libs.Count(l => l.Name?.StartsWith("org.lwjgl", StringComparison.OrdinalIgnoreCase) == true);

            if (fabric == 0) throw new InvalidOperationException("библиотеки Fabric потерялись при слиянии");
            if (lwjgl == 0) throw new InvalidOperationException("библиотеки Minecraft потерялись при слиянии");

            return $"id={merged.Id}, библиотек: {libs.Count} (fabric: {fabric}, lwjgl: {lwjgl})";
        });

        // 7. Сборка команды запуска
        await Run("Сборка аргументов запуска", async () =>
        {
            var node = await AppServices.Metadata.GetNodeAsync(summary.Url, ct);

            var instance = new InstanceProfile
            {
                Name = "selftest",
                MinecraftVersion = summary.Id,
                VersionJsonId = summary.Id,
                VersionType = summary.Type,
                VersionUrl = summary.Url,
                RamMb = 4096
            };
            instance.EnsureFolders();

            // резолвим наследование так же, как при реальном запуске
            var merged = await AppServices.Metadata.MergeNodesAsync(node, FindParentUrl, summary.Type, ct);

            var account = new Account { Type = "Offline", DisplayName = "Selftest" };
            var config = AppServices.Launch.Prepare(instance, merged, account);

            var all = string.Join(' ', config.JvmArgs.Concat([config.MainClass]).Concat(config.GameArgs));

            if (all.Contains("${", StringComparison.Ordinal))
                throw new InvalidOperationException("остались неподставленные ${...}");
            if (!all.Contains("--username Selftest", StringComparison.Ordinal))
                throw new InvalidOperationException("нет --username");
            if (!all.Contains("-Xmx", StringComparison.Ordinal))
                throw new InvalidOperationException("нет -Xmx");
            if (string.IsNullOrWhiteSpace(config.MainClass))
                throw new InvalidOperationException("пустой mainClass");

            return $"аргументов: {config.JvmArgs.Count + 1 + config.GameArgs.Count}, " +
                   $"classpath: {config.Classpath.Count}, mainClass: {config.MainClass}";
        });

        // 8. Оффлайн-UUID
        await Run("Оффлайн-UUID", () =>
        {
            var uuid = LaunchService.OfflineUuid("Notch");
            if (uuid.Length != 36) throw new InvalidOperationException($"неверный формат: {uuid}");
            return Task.FromResult(uuid);
        });

        // 9. Modrinth
        await Run("Modrinth API", async () =>
        {
            var projects = await AppServices.Modrinth.PopularAsync(summary.Id, null, 5, ct);
            if (projects.Count == 0) throw new InvalidOperationException("пустой ответ");
            return $"популярных модов: {projects.Count}, первый: {projects[0].Title}";
        });

        // 10. Версии загрузчиков
        await Run("Библиотеки старой версии (1.16.5: jar + natives)", async () =>
        {
            // До 1.17 Mojang описывает LWJGL двумя элементами с одинаковым artifact.path.
            // Если слияние их склеит, обычный jar не скачается и игра упадёт с
            // NoClassDefFoundError: org/lwjgl/glfw/GLFW — именно этот баг ловим.
            var manifest = await AppServices.Mojang.GetManifestAsync(false, ct);
            var summary = MojangApi.Find(manifest, "1.16.5")
                           ?? throw new FileNotFoundException("1.16.5 нет в манифесте");

            var node = await AppServices.Metadata.GetNodeAsync(summary.Url, ct);
            var merged = VersionMetadataService.MergeRaw(null, node);

            var libs = (merged["libraries"] as JsonArray) ?? [];
            var glfw = libs.OfType<JsonObject>()
                .Where(l => l["name"]?.GetValue<string>()?.StartsWith("org.lwjgl:lwjgl-glfw", StringComparison.Ordinal) == true)
                .ToList();

            var plain = glfw.Where(l => l["natives"] is null).ToList();
            var natives = glfw.Where(l => l["natives"] is JsonObject).ToList();
            var kinds = string.Join(", ", glfw.Select(l =>
                (l["natives"] is JsonObject ? "N" : "M") + (l["rules"] is JsonArray r ? "/" + r.Count.ToString() : "")));

            if (plain.Count == 0)
                throw new InvalidOperationException(
                    $"обычный lwjgl-glfw потерялся при слиянии (осталось {glfw.Count}: {kinds})");
            if (natives.Count == 0) throw new InvalidOperationException("natives lwjgl-glfw потерялся при слиянии");

            var jars = libs.OfType<JsonObject>()
                .Where(l => l["downloads"]?["artifact"]?["path"]?.GetValue<string>() is { } p &&
                            p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Select(l => l["downloads"]!["artifact"]!["path"]!.GetValue<string>())
                .Distinct()
                .ToList();

            var dupes = jars.GroupBy(j => j, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
            return $"библиотек в версии: {libs.Count}, обычных glfw: {plain.Count}, natives: {natives.Count}, дублей jar: {dupes}";
        });

        await Run("Каталог модов по категории", async () =>
        {
            var page = await AppServices.Modrinth.SearchPageAsync("", summary.Id, "fabric", "optimization", 0, 100, "downloads", ct);
            if (page.Hits.Count == 0) throw new InvalidOperationException("категория optimization пуста");

            var withIcons = page.Hits.Count(h => !string.IsNullOrEmpty(h.IconUrl));
            return $"всего в категории: {page.Total}, на странице: {page.Hits.Count}, с обложками: {withIcons}";
        });

await Run("CurseForge API (поиск модов)", async () =>
        {
            if (!AppServices.CurseForge.IsConfigured)
                throw new InvalidOperationException("API-ключ не задан");

            var mods = await AppServices.CurseForge.SearchAsync("sodium", "1.20.2", "fabric", null, 0, 5, ct);
            if (mods.Count == 0) throw new InvalidOperationException("пустой ответ");

            return $"найдено: {mods.Count}, первый: {mods[0].Name} ({mods[0].DownloadCount} загрузок)";
        });

        await Run("Установка мода с зависимостями", async () =>
        {
            var temp = new InstanceProfile
            {
                Name = "selftest-mods",
                MinecraftVersion = "1.20.2",
                VersionJsonId = "1.20.2",
                VersionType = "release",
                Loader = LoaderType.Fabric
            };

            try
            {
                temp.EnsureFolders();
                var files = await AppServices.Modrinth.GetCompatibleFilesAsync("AANobbMI", "1.20.2", LoaderType.Fabric, ct);
                var best = files.FirstOrDefault() ?? throw new InvalidOperationException("нет версий Sodium");

                var info = await AppServices.Modrinth.GetFileInfoAsync(best.Id, ct)
                    ?? throw new InvalidOperationException("нет информации о файле");

                var installed = await AppServices.Modrinth.InstallWithDependenciesAsync(info, temp, null, ct);
                var real = installed.Count(p => File.Exists(p));
                var markers = AppServices.Modpacks.ReadAllMarkers(temp.ModsDir).Count;

                return $"Sodium {info.VersionNumber}: файлов {installed.Count} (на диске {real}), метаданных {markers}";
            }
            finally
            {
                try { if (Directory.Exists(temp.RootDir)) Directory.Delete(temp.RootDir, true); }
                catch { /* уборка не критична */ }
            }
        });

        await Run("Списки версий загрузчиков", async () =>
        {
            var fabric = await AppServices.Loaders.GetLoaderVersionsAsync(LoaderType.Fabric, summary.Id, ct);
            var neoForge = await AppServices.Loaders.GetLoaderVersionsAsync(LoaderType.NeoForge, summary.Id, ct);
            return $"Fabric: {fabric.Count}, NeoForge: {neoForge.Count}" +
                   (neoForge.Count > 0 ? $" (последняя: {neoForge[0].Version})" : "");
        });

        _report.AppendLine(new string('-', 64));
        _report.AppendLine($"Итог: {passed} из {total} проверок пройдено");
        _report.AppendLine($"Данные лаунчера: {AppPaths.Root}");

        return passed == total;
    }

    public string WriteReport()
    {
        var path = Path.Combine(AppPaths.Root, "selftest.txt");
        File.WriteAllText(path, _report.ToString(), Encoding.UTF8);
        return path;
    }

    private static bool Evaluate(List<Rule> rules, RuleState state)
    {
        if (rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(state)) allowed = rule.Action == "allow";
        }

        return allowed;
    }
}