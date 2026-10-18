using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>Всплывающие уведомления в углу окна.</summary>
public sealed class Toast
{
    public required string Title { get; init; }
    public string Message { get; init; } = string.Empty;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public bool WithAction { get; init; }
    public Action? Action { get; init; }
    public string ActionText { get; init; } = "Открыть";
}

/// <summary>Очередь уведомлений: VM подписывается и показывает их в интерфейсе.</summary>
public sealed class ToastService
{
    public static ToastService Default { get; } = new();

    private readonly List<Action<Toast>> _subscribers = [];

    public IDisposable Subscribe(Action<Toast> handler)
    {
        _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Show(string title, string message = "", LogLevel level = LogLevel.Info,
        Action? action = null, string actionText = "Открыть")
    {
        var toast = new Toast
        {
            Title = title,
            Message = message,
            Level = level,
            Action = action,
            ActionText = actionText,
            WithAction = action is not null
        };

        foreach (var handler in _subscribers.ToArray())
        {
            try { handler(toast); }
            catch { /* подписчик не должен ломать логику */ }
        }
    }

    public void Success(string title, string message = "") => Show(title, message, LogLevel.Success);
    public void Error(string title, string message = "") => Show(title, message, LogLevel.Error);
    public void Info(string title, string message = "") => Show(title, message, LogLevel.Info);
    public void Warn(string title, string message = "") => Show(title, message, LogLevel.Warn);

    private sealed class Subscription(ToastService owner, Action<Toast> handler) : IDisposable
    {
        public void Dispose() => owner._subscribers.Remove(handler);
    }
}

/// <summary>
/// Асинхронная загрузка обложек модов с кэшем в памяти и на диске.
/// Обложки маленькие (64px), поэтому держать их в RAM дёшево.
/// </summary>
public static class ThumbnailLoader
{
    private const int MaxMemoryItems = 600;

    private static readonly ConcurrentDictionary<string, ImageSource> Memory = new();
    private static readonly SemaphoreSlim Throttle = new(6);

    public static ImageSource? TryGet(string? url) =>
        url is { Length: > 0 } && Memory.TryGetValue(url, out var cached) ? cached : null;

    public static async Task<ImageSource?> LoadAsync(string? url, int size = 64, CancellationToken ct = default)
    {
        if (url is not { Length: > 0 }) return null;
        if (Memory.TryGetValue(url, out var cached)) return cached;

        await Throttle.WaitAsync(ct);
        try
        {
            if (Memory.TryGetValue(url, out cached)) return cached;

            var bytes = await AppServices.Api.Http.GetByteArrayAsync(url, ct);
            var image = Decode(bytes, size);
            if (image is not null)
            {
                if (Memory.Count >= MaxMemoryItems) Memory.Clear();
                Memory[url] = image;
            }

            return image;
        }
        catch (Exception)
        {
            return null; // обложка — не критична
        }
        finally
        {
            Throttle.Release();
        }
    }

    public static ImageSource? Decode(byte[] bytes, int size)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = size;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Обложка не декодирована: {ex.Message}");
            return null;
        }
    }

    /// <summary>Заглушка: ромбик бренда, пока грузится обложка.</summary>
    public static ImageSource? Placeholder => NexaBrand.LogoImage();
}