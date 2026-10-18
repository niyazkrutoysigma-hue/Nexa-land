using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftLauncher.Models;

/// <summary>Состояние для вычисления правил запуска (features).</summary>
public sealed class RuleState
{
    public bool IsDemoUser { get; init; }
    public bool HasCustomResolution { get; init; }
    public bool HasQuickPlaysSupport { get; init; }
    public bool IsQuickPlayMultiplayer { get; init; }
    public bool IsQuickPlaySingleplayer { get; init; }
    public bool IsQuickPlayRealms { get; init; }

    public static RuleState Default { get; } = new();
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }

    public bool Matches()
    {
        if (!string.IsNullOrEmpty(Name) && !string.Equals(Name, "windows", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(Arch))
        {
            var realArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                .ToString().ToLowerInvariant(); // x64 / arm64
            var ruleArch = Arch.ToLowerInvariant() switch
            {
                "x86" => "x86",
                "x86_64" => "x64",
                "arm64" => "arm64",
                _ => Arch.ToLowerInvariant()
            };
            if (ruleArch != "any" && ruleArch != realArch) return false;
        }

        return true;
    }
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }

    public bool Matches(RuleState state)
    {
        if (Os is not null && !Os.Matches()) return false;
        if (Features is null || Features.Count == 0) return true;

        foreach (var (key, value) in Features)
        {
            var actual = key switch
            {
                "is_demo_user" => state.IsDemoUser,
                "has_custom_resolution" => state.HasCustomResolution,
                "has_quick_plays_support" => state.HasQuickPlaysSupport,
                "is_quick_play_multiplayer" => state.IsQuickPlayMultiplayer,
                "is_quick_play_singleplayer" => state.IsQuickPlaySingleplayer,
                "is_quick_play_realms" => state.IsQuickPlayRealms,
                _ => false
            };
            if (actual != value) return false;
        }

        return true;
    }
}

public sealed class Artifact
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("classifier")] public string? Classifier { get; set; }
}

public sealed class LibraryDef
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("rules")] public List<Rule> Rules { get; set; } = [];
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("extract")] public ExtractDef? Extract { get; set; }
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }

    public bool IsNative => Natives is { Count: > 0 };
    public string? NativeClassifier => Natives?.GetValueOrDefault("windows");

    /// <summary>Имя в формате group:artifact:version[:classifier].</summary>
    public string[] NameParts => (Name ?? string.Empty).Split(':');

    public string MavenPath
    {
        get
        {
            if (!string.IsNullOrEmpty(Url)) return Url;
            var parts = NameParts;
            if (parts.Length < 3) return string.Empty;

            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? parts[3] : null;
            var ext = artifact.Contains('@') ? artifact[(artifact.IndexOf('@') + 1)..] : "jar";
            var file = $"{artifact}-{version}{(classifier is null ? "" : "-" + classifier)}.{ext}";
            return $"{group}/{artifact}/{version}/{file}";
        }
    }

    /// <summary>Artifact-объект для текущей ОС (в старых версиях — natives).</summary>
    public Artifact? GetArtifact(RuleState state)
    {
        if (Downloads is not null && Downloads.Classifiers is { Count: > 0 })
        {
            var classifier = NativeClassifier;
            if (!string.IsNullOrEmpty(classifier) && Downloads.Classifiers.TryGetValue(classifier, out var native))
                return native;
        }

        return Downloads?.Artifact;
    }
}

public sealed class ExtractDef
{
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = [];
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public Artifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, Artifact>? Classifiers { get; set; }
}

public sealed class LoggingDef
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public Artifact? File { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class ArgumentElement
{
    public List<Rule> Rules { get; set; } = [];
    public List<string> Value { get; set; } = [];

    public bool IsAllowed(RuleState state)
    {
        var include = false;
        foreach (var rule in Rules)
        {
            if (rule.Matches(state)) include = rule.Action == "allow";
        }
        return include;
    }
}

public sealed class ArgumentsDef
{
    [JsonIgnore] public List<ArgumentElement> Game { get; set; } = [];
    [JsonIgnore] public List<ArgumentElement> Jvm { get; set; } = [];
}

/// <summary>Распарсенный version.json с учётом наследования (inheritsFrom).</summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("assetIndex")] public Artifact? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("libraries")] public List<LibraryDef>? Libraries { get; set; }
    [JsonPropertyName("logging")] public Dictionary<string, LoggingDef>? Logging { get; set; }
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; set; }
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionDef? JavaVersion { get; set; }

    [JsonIgnore] public string VersionType { get; set; } = "release";

    public LoggingDef? ClientLogging => Logging?.GetValueOrDefault("client");
    public int JavaMajor => JavaVersion?.MajorVersion ?? 0;

    public bool IsLegacyArguments => !string.IsNullOrEmpty(MinecraftArguments);
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")] public Artifact? Client { get; set; }
    [JsonPropertyName("server")] public Artifact? Server { get; set; }
}

public sealed class JavaVersionDef
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

/// <summary>Запись из version_manifest_v2.json.</summary>
public sealed class VersionSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "release";
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("time")] public DateTime ReleaseTime { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }

    [JsonIgnore] public bool IsSnapshot => Type is "snapshot";

    public string DisplayType => Type switch
    {
        "release" => "Релиз",
        "snapshot" => "Снапшот",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Type
    };

    /// <summary>Какая версия Java нужна (major).</summary>
    public int RequiredJava => JavaRequirements.For(Id);

    /// <summary>Человекочитаемая дата релиза.</summary>
    public string DateText => ReleaseTime == default ? "" : ReleaseTime.ToString("dd.MM.yyyy");
}

public static class JavaRequirements
{
    public static int For(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return 8;

        var core = versionId.Split('-')[0].Split(' ')[0];
        if (!Version.TryParse(core, out var v)) return 8;

        if (v.Major == 1)
        {
            if (v.Minor >= 20 && v.Build >= 5) return 21; // 1.20.5+ и все 1.21+
            if (v.Minor == 17) return 16;
            if (v.Minor >= 18) return 17;
            return 8; // <= 1.16.5
        }

        return v.Major >= 9 ? v.Major : 8;
    }
}