using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Common;
using MinecraftLauncher.Models;

namespace MinecraftLauncher.Services;

/// <summary>Генерация иконок инстансов и загрузка аватаров (WPF-рендеринг, без внешних пакетов).</summary>
public sealed class IconService
{
    private readonly ApiClient _api;

    public IconService(ApiClient api) => _api = api;

    /// <summary>Иконка-блок с градиентом группы и первой буквой названия.</summary>
    public void GenerateInstanceIcon(InstanceProfile instance)
    {
        try
        {
            var (c1, c2) = instance.Loader switch
            {
                LoaderType.Fabric => (Color.FromRgb(0xDB, 0xD0, 0xB4), Color.FromRgb(0xB4, 0x9E, 0x64)),
                LoaderType.Quilt => (Color.FromRgb(0xB0, 0xD3, 0xE0), Color.FromRgb(0x6E, 0x9C, 0xB0)),
                LoaderType.Forge => (Color.FromRgb(0x4A, 0x6B, 0x8A), Color.FromRgb(0x2B, 0x3E, 0x52)),
                LoaderType.NeoForge => (Color.FromRgb(0xE8, 0x6C, 0x3A), Color.FromRgb(0xA8, 0x3B, 0x14)),
                _ => (Color.FromRgb(0x7A, 0xC1, 0x4B), Color.FromRgb(0x46, 0x7C, 0x28))
            };

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var gradient = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
                gradient.Freeze();
                dc.DrawRectangle(gradient, null, new Rect(0, 0, 128, 128));

                // лёгкий шум для «кубного» вида
                var random = new Random(instance.Id.GetHashCode());
                for (var i = 0; i < 700; i++)
                {
                    var x = random.Next(8, 120);
                    var y = random.Next(8, 120);
                    var shade = new SolidColorBrush(Color.FromArgb((byte)random.Next(8, 34), (byte)0, (byte)0, (byte)0));
                    shade.Freeze();
                    dc.DrawRectangle(shade, null, new Rect(x, y, 8, 8));
                }

                var letter = string.IsNullOrWhiteSpace(instance.Name)
                    ? "?"
                    : char.ToUpperInvariant(instance.Name.Trim()[0]).ToString();

                var text = new FormattedText(letter, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 62, Brushes.White, 1.0);
                var origin = new Point(64 - text.Width / 2, 64 - text.Height / 2 - 2);
                var shadow = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
                shadow.Freeze();

                dc.DrawText(new FormattedText(letter, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 62, shadow, 1.0),
                    new Point(origin.X + 2, origin.Y + 2));
                dc.DrawText(text, origin);
            }

            SavePng(visual, 128, 128, instance.IconFile);
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось создать иконку: {ex.Message}");
        }
    }


    private static void SavePng(DrawingVisual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public async Task DownloadAvatarAsync(Account account, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.AvatarUrl)) return;
        try
        {
            var dest = Path.Combine(AppPaths.CacheDir, "avatars", account.Id + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) return;
            await _api.DownloadFileAsync(account.AvatarUrl!, dest, null, 0, null, ct, "Аватар");
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Аватар не загружен: {ex.Message}");
        }
    }

    public string? GetAvatarPath(Account account)
    {
        var path = Path.Combine(AppPaths.CacheDir, "avatars", account.Id + ".png");
        return File.Exists(path) ? path : null;
    }
}

/// <summary>Обновление самого лаунчера через GitHub Releases.</summary>
public sealed class SelfUpdateService
{
    private readonly ApiClient _api;

    public SelfUpdateService(ApiClient api) => _api = api;

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = [];
    }

    public sealed class UpdateInfo
    {
        public required string Version { get; init; }
        public required string Url { get; init; }
        public string Notes { get; init; } = string.Empty;
        public long Size { get; init; }
        public bool IsNewer { get; init; }
    }

    public string CurrentVersion => typeof(SelfUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var repo = SettingsService.Default.SelfUpdateRepo;
        if (string.IsNullOrWhiteSpace(repo))
            return null;

        try
        {
            var release = await _api.GetJsonAsync<Release>(
                $"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (release is null || release.Draft) return null;

            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? release.Assets.FirstOrDefault();
            if (asset is null) return null;

            var version = release.TagName.TrimStart('v');
            return new UpdateInfo
            {
                Version = version,
                Url = asset.Url,
                Notes = release.Body,
                Size = asset.Size,
                IsNewer = IsNewer(version, CurrentVersion)
            };
        }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Проверка обновлений не удалась: {ex.Message}");
            return null;
        }
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.DownloadsDir, Path.GetFileName(update.Url));
        await _api.DownloadFileAsync(update.Url, path, null, update.Size, null, ct, "Обновление");
        LogService.Default.Success($"Обновление скачано: {path}");
        return path;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Normalize(candidate), out var a)) return false;
        if (!Version.TryParse(Normalize(current), out var b)) return false;
        return a > b;
    }

    private static string Normalize(string value) =>
        string.Join('.', value.Split('.').Take(3).Select(p => int.TryParse(p, out var n) ? n : 0));
}