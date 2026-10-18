using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

public sealed class LaunchConfig
{
    public required InstanceProfile Instance { get; init; }
    public required Account Account { get; init; }
    public VersionJson Version { get; set; } = new();
    public string? NativesDir { get; set; }
    public required List<string> Classpath { get; init; }
    public required List<string> JvmArgs { get; init; }
    public required List<string> GameArgs { get; init; }
    public required string JavaPath { get; init; }
    public string MainClass { get; set; } = string.Empty;
}

/// <summary>Сборка команды запуска и запуск процесса игры.</summary>
public sealed class LaunchService
{
    private static readonly Regex Placeholder = new(@"\$\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    private readonly ApiClient _api;
    private readonly VersionMetadataService _metadata;
    private readonly JavaLocator _javaLocator;
    private readonly AdoptiumService _adoptium;
    private readonly AccountService _accounts;
    private readonly SettingsService _settings = SettingsService.Default;

    private readonly Dictionary<string, Process> _running = [];

    public event Action<string>? InstanceStarted;
    public event Action<string>? InstanceStopped;

    public IEnumerable<string> RunningInstances => _running.Keys;

    public bool IsRunning(string instanceId) => _running.ContainsKey(instanceId);

    public LaunchService(ApiClient api, VersionMetadataService metadata, JavaLocator javaLocator,
        AdoptiumService adoptium, AccountService accounts)
    {
        _api = api;
        _metadata = metadata;
        _javaLocator = javaLocator;
        _adoptium = adoptium;
        _accounts = accounts;
    }

    /// <summary>Загружает version.json инстанса с разрешением inheritsFrom.</summary>
    public async Task<VersionJson> ResolveVersionAsync(InstanceProfile instance, CancellationToken ct = default)
    {
        var node = await _metadata.GetNodeAsync(instance.VersionUrl, ct);
        return await _metadata.MergeNodesAsync(node, FindVanillaUrlAsync, instance.VersionType, ct);
    }

    /// <summary>Ищет url ванильной версии в манифесте Mojang (для разрешения inheritsFrom).</summary>
    private async Task<string?> FindVanillaUrlAsync(string versionId)
    {
        var manifest = await new MojangApi(_api).GetManifestAsync(false);
        return MojangApi.Find(manifest, versionId)?.Url;
    }

    /// <summary>Classpath: разрешённые библиотеки + natives исключены + клиентский jar последним.</summary>
    public List<string> BuildClasspath(VersionJson version, InstanceProfile instance)
    {
        var state = RuleState.Default;
        var classpath = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in version.Libraries ?? [])
        {
            if (lib.Rules.Count > 0 && !Evaluate(lib.Rules, state)) continue;
            if (lib.IsNative) continue;

            var artifact = lib.GetArtifact(state);
            string? path;
            if (artifact?.Path is { Length: > 0 } artifactPath) path = artifactPath;
            else path = lib.MavenPath;
            if (string.IsNullOrEmpty(path)) continue;

            var full = Path.Combine(instance.LibraryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && seen.Add(full)) classpath.Add(full);
            else if (!File.Exists(full))
                LogService.Default.Trace($"Библиотека ещё не скачана: {path}");
        }

        var clientJar = instance.ClientJarPath;
        if (File.Exists(clientJar)) classpath.Add(clientJar);

        return classpath;
    }

    /// <summary>
    /// Папка для нативных библиотек. Она должна существовать всегда: в version.json есть
    /// -Djna.tmpdir / -Dorg.lwjgl.system.SharedLibraryExtractPath, и при пустом значении
    /// JNA падает с «temporary directory '' does not exist».
    /// </summary>
    /// <summary>
    /// Какие библиотеки версии ещё не скачаны. Если список не пуст, игра стартует,
    /// но упадёт с NoClassDefFoundError — поэтому проверяем до запуска.
    /// </summary>
    public List<string> MissingLibraries(VersionJson version, InstanceProfile instance)
    {
        var state = RuleState.Default;
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in version.Libraries ?? [])
        {
            if (lib.Rules.Count > 0 && !EvaluateRules(lib.Rules, state)) continue;
            if (lib.IsNative) continue;

            var artifact = lib.GetArtifact(state);
            var path = artifact?.Path is { Length: > 0 } p ? p
                : !string.IsNullOrEmpty(lib.Name) ? lib.MavenPath
                : string.Empty;
            if (path.Length == 0) continue;

            var full = Path.Combine(instance.LibraryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && seen.Add(full)) missing.Add(full);
        }

        if (!File.Exists(instance.ClientJarPath)) missing.Add(instance.ClientJarPath);

        return missing;
    }

    private static bool EvaluateRules(List<Rule> rules, RuleState state)
    {
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(state)) allowed = rule.Action == "allow";
        }
        return allowed;
    }

    public string NativesDirectory(InstanceProfile instance, VersionJson version)
    {
        var dir = Path.Combine(instance.VersionsRoot, version.Id, "natives");
        AppPaths.Ensure(dir);
        return dir;
    }

    /// <summary>Собирает финальную команду запуска.</summary>
    public LaunchConfig Prepare(InstanceProfile instance, VersionJson version, Account account)
    {
        var classpath = BuildClasspath(version, instance);
        var natives = NativesDirectory(instance, version);

        var ram = Math.Clamp(instance.RamMb, 512, Math.Max(1024, _settings.TotalRamMb - 512));
        var jvmArgs = new List<string> { $"-Xmx{ram}M" };
        var initial = (int)Math.Min(ram * 0.5, 1024);
        jvmArgs.Add($"-Xms{initial}M");

        jvmArgs.Add("-XX:+UseG1GC");
        jvmArgs.Add("-XX:+UnlockExperimentalVMOptions");
        jvmArgs.Add("-XX:G1NewSizePercent=30");
        jvmArgs.Add("-XX:MaxGCPauseMillis=100");
        jvmArgs.Add("-XX:G1HeapRegionSize=8M");
        jvmArgs.Add("-XX:+DisableExplicitGC");
        jvmArgs.Add("-XX:MaxTenuringThreshold=1");
        jvmArgs.Add("-Dsun.rise.meters=false");
        jvmArgs.Add("-Dsun.java2d.metal=true");
        jvmArgs.Add("-Dfile.encoding=UTF-8");

        if (!string.IsNullOrWhiteSpace(instance.JavaArgs))
            jvmArgs.AddRange(SplitArguments(instance.JavaArgs));

        var state = RuleState.Default;
        var replacements = BuildReplacements(instance, version, account, classpath, natives);

        if (version.IsLegacyArguments)
        {
            jvmArgs.Add("-Djava.library.path=" + natives);
            jvmArgs.Add("-cp");
            jvmArgs.Add(string.Join(Path.PathSeparator, classpath));

            var legacy = Substitute(version.MinecraftArguments!, replacements);
            var gameArgs = SplitArguments(legacy);
            AddExtraGameArgs(gameArgs, instance);
            return Finish(instance, version, account, jvmArgs, gameArgs, replacements);
        }

        var arguments = ArgumentParser.Parse(version.Arguments);
        foreach (var element in arguments.Jvm)
        {
            if (element.Rules.Count > 0 && !element.IsAllowed(state)) continue;
            foreach (var arg in element.Value)
                jvmArgs.Add(Substitute(arg, replacements));
        }

        var finalGameArgs = new List<string>();
        foreach (var element in arguments.Game)
        {
            if (element.Rules.Count > 0 && !element.IsAllowed(state)) continue;
            foreach (var arg in element.Value)
                finalGameArgs.Add(Substitute(arg, replacements));
        }

        AddExtraGameArgs(finalGameArgs, instance);
        return Finish(instance, version, account, jvmArgs, finalGameArgs, replacements);
    }

    private LaunchConfig Finish(InstanceProfile instance, VersionJson version, Account account,
        List<string> jvmArgs, List<string> gameArgs, Dictionary<string, string> replacements)
    {
        var java = ResolveJavaPath(instance, version);
        var mainClass = Substitute(version.MainClass ?? "net.minecraft.client.main.Main", replacements);

        return new LaunchConfig
        {
            Instance = instance,
            Account = account,
            Version = version,
            Classpath = BuildClasspath(version, instance),
            JvmArgs = jvmArgs,
            GameArgs = gameArgs,
            JavaPath = java,
            MainClass = mainClass,
            NativesDir = NativesDirectory(instance, version)
        };
    }

    private static void AddExtraGameArgs(List<string> gameArgs, InstanceProfile instance)
    {
        if (string.IsNullOrWhiteSpace(instance.MinecraftArgs)) return;
        gameArgs.AddRange(SplitArguments(instance.MinecraftArgs));
    }

    public string ResolveJavaPath(InstanceProfile instance, VersionJson version)
    {
        var required = version.JavaMajor > 0 ? version.JavaMajor : JavaRequirements.For(instance.MinecraftVersion);

        // 1. Явно заданная Java инстанса
        if (!string.IsNullOrWhiteSpace(instance.JavaPath) && !instance.JavaPath.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var probe = JavaLocator.Probe(instance.JavaPath);
            if (probe is not null) return probe.Path;

            var exe = instance.JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)
                ? instance.JavaPath
                : Path.Combine(instance.JavaPath, "bin", "java.exe");
            if (File.Exists(exe)) return exe;
        }

        // 2. Java из настроек — только если её версия подходит версии Minecraft
        if (!string.IsNullOrWhiteSpace(_settings.JavaPath) && !_settings.JavaPath.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var exe = _settings.JavaPath.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)
                ? _settings.JavaPath
                : Path.Combine(_settings.JavaPath, "bin", "java.exe");

            if (File.Exists(exe))
            {
                var info = JavaLocator.Probe(exe);
                if (info is null || info.Major == required)
                    return exe;

                // 1.16.5 на Java 17 и 21 не стартует, а 1.20+ на Java 8 — тем более:
                // версия не совпала, идём дальше и ищем подходящую автоматически.
                LogService.Default.Info(
                    $"В настройках указана {info?.Name ?? "Java"}, а версии нужно Java {required} — ищем подходящую");
            }
        }

        // 3. Поиск на машине
        var found = _javaLocator.FindBest(required);
        if (found is not null)
        {
            LogService.Default.Info($"Используем {found.Name}");
            return found.Path;
        }

        // 4. Java, скачанная самим лаунчером: ищем по версии, а не по имени папки
        foreach (var home in ManagedJavaHomes(required))
        {
            LogService.Default.Info($"Используем скачанную лаунчером Java: {home}");
            return home;
        }

        throw new JavaNotFoundException(required);
    }

    /// <summary>Папки Java в %AppData%\NexaLand\java, подходящие под требуемую версию.</summary>
    private static IEnumerable<string> ManagedJavaHomes(int required)
    {
        if (!Directory.Exists(AppPaths.JavaRoot)) yield break;

        foreach (var folder in Directory.EnumerateDirectories(AppPaths.JavaRoot))
        {
            // распакованный архив лежит в folder\<jdk-x.y.z>
            var candidates = new[] { folder }
                .Concat(Directory.Exists(folder)
                    ? Directory.EnumerateDirectories(folder)
                    : Array.Empty<string>());

            foreach (var home in candidates)
            {
                var probe = JavaLocator.Probe(home);
                if (probe is not null && probe.Major == required) yield return probe.Path;
            }
        }
    }

    public async Task<JavaLocator.JavaInfo> EnsureJavaAsync(int required, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var found = _javaLocator.FindBest(required);
        if (found is not null) return found;

        var downloaded = await _adoptium.DownloadJavaAsync(required, progress, ct)
            ?? throw new JavaNotFoundException(required);
        _javaLocator.Refresh();
        return downloaded;
    }

    private Dictionary<string, string> BuildReplacements(InstanceProfile instance, VersionJson version,
        Account account, List<string> classpath, string? natives)
    {
        var versionType = instance.VersionType switch
        {
            "old_beta" => "old_beta",
            "old_alpha" => "old_alpha",
            "snapshot" => "snapshot",
            _ => "release"
        };

        var uuid = account.Uuid;
        var token = account.AccessToken ?? "0";
        var userType = account.IsMicrosoft ? "msa" : "legacy";

        if (!account.IsMicrosoft)
        {
            uuid = OfflineUuid(account.DisplayName);
            token = "0";
            userType = "legacy";
        }

        return new Dictionary<string, string>
        {
            ["auth_player_name"] = account.DisplayName,
            ["version_name"] = instance.VersionJsonId,
            ["game_directory"] = instance.GameDir,
            ["assets_root"] = instance.AssetsRoot,
            ["assets_index_name"] = version.AssetIndex?.Path?.Replace("minecraft/", "") is { Length: > 0 } id
                ? id
                : (version.AssetIndex?.Id ?? instance.MinecraftVersion),
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = token,
            ["auth_session"] = "token:" + token + ":" + uuid,
            ["user_type"] = userType,
            ["version_type"] = versionType,
            ["resolution_width"] = "854",
            ["resolution_height"] = "480",
            ["clientid"] = account.Xuid ?? "",
            ["auth_xuid"] = account.Xuid ?? "",
            ["user_properties"] = "{}",
            ["game_assets"] = instance.AssetsRoot,
            ["library_directory"] = instance.LibraryRoot,
            ["natives_directory"] = natives ?? "",
            ["launcher_name"] = _settings.LauncherBrand,
            ["launcher_version"] = "1.0",
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = string.Join(Path.PathSeparator, classpath)
        };
    }

    public static string Substitute(string text, Dictionary<string, string> replacements) =>
        Placeholder.Replace(text, m => replacements.GetValueOrDefault(m.Groups[1].Value, m.Value));

    public static List<string> SplitArguments(string args) =>
        args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>UUID оффлайн-игрока (как делает vanilla).</summary>
    public static string OfflineUuid(string name)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        md5[6] = (byte)((md5[6] & 0x0F) | 0x30);
        md5[8] = (byte)((md5[8] & 0x3F) | 0x80);
        return new Guid(md5).ToString();
    }

    public async Task<int> LaunchAsync(LaunchConfig config, CancellationToken ct = default)
    {
        var instance = config.Instance;

        if (_running.ContainsKey(instance.Id))
            throw new InvalidOperationException("Игра для этого инстанса уже запущена");

        var args = new List<string>();
        args.AddRange(config.JvmArgs);
        args.Add(config.MainClass);
        args.AddRange(config.GameArgs);

        var startInfo = new ProcessStartInfo
        {
            FileName = config.JavaPath,
            WorkingDirectory = instance.GameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        if (SettingsService.Default.CloseAfterLaunch)
            startInfo.Environment["JAVA_TOOL_OPTIONS"] = "";

        LogService.Default.Info($"Запуск: {instance.Name} ({instance.DisplayVersion}), RAM {instance.RamMb} МБ");
        LogService.Default.Info($"Java: {config.JavaPath}");
        LogService.Default.Trace(string.Join(' ', args));

        var process = Process.Start(startInfo) ?? throw new IOException("Не удалось запустить процесс Java");
        _running[instance.Id] = process;

        instance.LastPlayed = DateTime.Now;
        InstanceStarted?.Invoke(instance.Id);

        var logFile = Path.Combine(instance.GameDir, "logs", $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        StreamWriter? writer = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            writer = new StreamWriter(logFile, append: false);
        }
        catch (Exception ex)
        {
            // лог игры не критичен: не даём ему сломать запуск
            LogService.Default.Warn($"Не удалось создать лог запуска: {ex.Message}");
        }

        void Pump(StreamReader reader, LogLevel level)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var text = line.Trim();
                if (text.Length == 0) continue;
                LogService.Default.Write(level, text);
                if (writer is null) continue;
                lock (writer) writer.WriteLine(text);
            }
        }

        var outTask = Task.Run(() => Pump(process.StandardOutput, LogLevel.Trace));
        var errTask = Task.Run(() => Pump(process.StandardError, LogLevel.Error));

        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAny(outTask, errTask);
        }
        catch (OperationCanceledException)
        {
            // лаунчер закрывается — оставляем игру жить
        }
        finally
        {
            _running.Remove(instance.Id);
            if (writer is not null)
            {
                lock (writer) writer.Flush();
                writer.Dispose();
            }
            InstanceStopped?.Invoke(instance.Id);
        }

        var code = process.ExitCode;
        LogService.Default.Write(code == 0 ? LogLevel.Success : LogLevel.Warn,
            $"Игра завершилась с кодом {code}. Лог: {logFile}");

        if (code != 0)
        {
            instance.LastError = DescribeExitCode(code);
        }
        else
        {
            instance.LastError = null;
        }

        return code;
    }

    private static string DescribeExitCode(int code) => code switch
    {
        -1 => "Крайняя ошибка в JVM (увеличьте RAM или проверьте Java).",
        1 => "Ошибка при запуске: чаще всего несовместимые моды или нехватка памяти.",
        -1073740791 => "Java упала (0xC0000409). Попробуйте другую версию Java.",
        _ => $"Код выхода {code}"
    };

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

public sealed class JavaNotFoundException(int requiredMajor)
    : Exception($"Не найдена Java {requiredMajor}. Установите Temurin через лаунчер или укажите путь вручную.")
{
    public int RequiredMajor { get; } = requiredMajor;
}