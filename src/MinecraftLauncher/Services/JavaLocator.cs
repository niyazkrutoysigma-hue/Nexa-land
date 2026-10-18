using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Поиск установленных Java (JDK/JRE) на машине + скачивание Temurin.</summary>
public sealed class JavaLocator
{
    public sealed record JavaInfo(string Path, string Version, int Major, string Vendor)
    {
        public string Name => $"{Vendor} {Version} (Java {Major})";
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;
    }

    private readonly List<JavaInfo> _found = [];
    private DateTime _scannedAt = DateTime.MinValue;

    public IReadOnlyList<JavaInfo> Found => _found;

    public void Refresh()
    {
        _found.Clear();
        _found.AddRange(Scan());
        _scannedAt = DateTime.Now;
        LogService.Default.Info($"Обнаружено версий Java: {_found.Count}");
    }

    public IEnumerable<JavaInfo> Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Скачанные нами JDK в %AppData%\MinecraftLauncher\java
        foreach (var dir in SafeDirs(AppPaths.JavaRoot))
        {
            var info = Probe(dir);
            if (info is not null && seen.Add(info.Path)) yield return info;
        }

        // 2. Реестр
        foreach (var key in new[]
                 {
                     @"SOFTWARE\JavaSoft\JDK",
                     @"SOFTWARE\JavaSoft\Java Development Kit",
                     @"SOFTWARE\JavaSoft\Java Runtime Environment",
                     @"SOFTWARE\JavaSoft\JRE",
                     @"SOFTWARE\Eclipse Adoptium\JDK",
                     @"SOFTWARE\Amazon Corretto",
                     @"SOFTWARE\Azul Zulu",
                     @"SOFTWARE\Microsoft\JDK",
                     @"SOFTWARE\WOW6432Node\JavaSoft\JDK"
                 })
        {
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    foreach (var path in ReadRegistryPaths(hive, view, key))
                    {
                        var info = Probe(path);
                        if (info is not null && seen.Add(info.Path)) yield return info;
                    }
                }
            }
        }

        // 3. Типовые каталоги
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
                 })
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;

            foreach (var vendor in SafeDirs(baseDir))
            {
                foreach (var candidate in SafeDirs(vendor))
                {
                    // пропускаем вложенные каталоги второго уровня кроме известных
                    if (!File.Exists(Path.Combine(candidate, "bin", "java.exe")) &&
                        !File.Exists(Path.Combine(candidate, "bin", "javaw.exe")))
                    {
                        foreach (var deeper in SafeDirs(candidate).Take(6))
                        {
                            var deep = Probe(deeper);
                            if (deep is not null && seen.Add(deep.Path)) yield return deep;
                        }
                        continue;
                    }

                    var info = Probe(candidate);
                    if (info is not null && seen.Add(info.Path)) yield return info;
                }
            }
        }

        // 4. JAVA_HOME и PATH
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var info = Probe(javaHome);
            if (info is not null && seen.Add(info.Path)) yield return info;
        }
    }

    /// <summary>
    /// Java под требуемую версию Minecraft.
    /// Для старых версий (Java 8 — это всё до 1.16.5 включительно) подходит только ровно Java 8:
    /// на Java 17/21 они падают или ведут себя иначе, поэтому вместо «ближайшей» возвращаем null,
    /// и вызывающий код скачает нужную JRE сам. Для современных версий берём минимальную >= нужной.
    /// </summary>
    public JavaInfo? FindBest(int requiredMajor)
    {
        if (_scannedAt == DateTime.MinValue) Refresh();

        // точное совпадение версии приоритетнее всего; при нескольких —
        // сначала «чистые» JDK/JRE, потом те, что встроены в IDE
        var exact = _found.Where(j => j.Major == requiredMajor)
            .OrderBy(j => IsIdeRuntime(j.Path))
            .FirstOrDefault();
        if (exact is not null) return exact;

        // Minecraft ≤ 1.16.5 требует Java 8 и не работает на новых
        if (requiredMajor <= 8) return null;

        return _found
            .Where(j => j.Major > requiredMajor)
            .OrderBy(j => j.Major)
            .ThenBy(j => IsIdeRuntime(j.Path))
            .FirstOrDefault();
    }

    /// <summary>
    /// Java, встроенная в IDE (Android Studio, IntelliJ, Visual Studio Code), — последний выбор:
    /// такие сборки часто обновляются/удаляются вместе с IDE и могут быть заблокированы
    /// политикой безопасности, поэтому лаунчер сначала берёт обычные JDK/JRE (Temurin, Oracle, Microsoft).
    /// </summary>
    private static bool IsIdeRuntime(string path)
    {
        var p = path.Replace('/', '\\');
        string[] markers =
        {
            "\\Android Studio\\", "\\IntelliJ", "\\JetBrains", "\\.jdks\\", "\\.gradle\\",
            "\\Visual Studio Code\\", "\\Code\\", "\\Microsoft Visual Studio\\", "\\Unity"
        };

        return markers.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public static JavaInfo? Probe(string javaHome)
    {
        var exe = Path.Combine(javaHome, "bin", "java.exe");
        if (!File.Exists(exe)) exe = Path.Combine(javaHome, "bin", "javaw.exe");
        if (!File.Exists(exe)) return null;

        var (version, vendor) = ReadRelease(javaHome);
        var major = ParseMajor(version, javaHome);
        if (major == 0) return null;

        return new JavaInfo(exe, version, major, vendor);
    }

    private static (string Version, string Vendor) ReadRelease(string javaHome)
    {
        var release = Path.Combine(javaHome, "release");
        if (!File.Exists(release)) return ("unknown", "Unknown");

        var version = "unknown";
        var vendor = "Unknown";
        foreach (var line in File.ReadAllLines(release))
        {
            if (line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                version = line["JAVA_VERSION=".Length..].Trim('"', ' ');
            else if (line.StartsWith("IMPLEMENTOR=", StringComparison.OrdinalIgnoreCase))
                vendor = line["IMPLEMENTOR=".Length..].Trim('"', ' ');
        }

        return (version, vendor);
    }

    private static int ParseMajor(string version, string javaHome)
    {
        var cleaned = version.Trim('"', ' ');
        if (cleaned.StartsWith("1.", StringComparison.Ordinal))
            cleaned = cleaned[2..];

        var dot = cleaned.IndexOf('.');
        if (dot > 0) cleaned = cleaned[..dot];

        if (int.TryParse(cleaned, out var major)) return major;

        // запасной путь: java -version
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(javaHome, "bin", "java.exe"))
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-version");
            using var process = System.Diagnostics.Process.Start(start);
            var output = process?.StandardError.ReadToEnd() ?? "";
            process?.WaitForExit(8000);
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(?:openjdk|java) version ""([\d._]+)""");
            if (!match.Success) return 0;
            var value = match.Groups[1].Value.Trim('"', ' ');
            if (value.StartsWith("1.")) value = value[2..];
            var d = value.IndexOf('.');
            if (d > 0) value = value[..d];
            return int.TryParse(value, out var m) ? m : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Пути установленных JDK из ветки реестра (значение по умолчанию в подключах).</summary>
    private static IEnumerable<string> ReadRegistryPaths(Microsoft.Win32.RegistryHive hive,
        Microsoft.Win32.RegistryView view, string key)
    {
        var result = new List<string>();
        try
        {
            using var hiveKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var baseKey = hiveKey.OpenSubKey(key);
            if (baseKey is null) return result;

            // часть вендоров пишет путь сразу в значение ветки
            if (baseKey.GetValue("") is string direct && direct.Contains('\\')) result.Add(direct);

            foreach (var subKey in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var child = baseKey.OpenSubKey(subKey);
                    if (child?.GetValue("") is string path && path.Contains('\\')) result.Add(path);
                }
                catch
                {
                    // отдельная ветка может быть недоступна — просто пропускаем
                }
            }
        }
        catch
        {
            // ветки может не быть
        }

        return result;
    }

    private static IEnumerable<string> SafeDirs(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return [];
        try
        {
            return Directory.GetDirectories(baseDir).OrderByDescending(x => x).Take(80).ToArray();
        }
        catch
        {
            return [];
        }
    }
}