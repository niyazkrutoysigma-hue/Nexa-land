using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>Прогресс загрузки файла.</summary>
public sealed record DownloadStatus(string FileName, string Phase, long Total, long Received)
{
    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;
    public string SizeText => Format(Received) + " / " + Format(Total);

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} МБ",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} ГБ"
    };
}

/// <summary>Единая точка доступа в сеть: ретраи, User-Agent, проверка SHA-1, докачка.</summary>
public sealed class ApiClient : IDisposable
{
    public const string UserAgent = "MinecraftLauncher/1.0 (Windows; +https://localhost)";

    private readonly HttpClient _http;
    private readonly string _tempDir;

    public ApiClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _tempDir = AppPaths.TempDir;
    }

    public HttpClient Http => _http;

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        if (IsLocalFile(url))
            return await File.ReadAllTextAsync(ToLocalPath(url), ct);

        using var response = await SendWithRetryAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Локальные файлы вида file:///C:/... используются для установленных загрузчиками версий.</summary>
    public static bool IsLocalFile(string url) =>
        url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("local://", StringComparison.OrdinalIgnoreCase);

    public static string ToLocalPath(string url) => new Uri(url).LocalPath;

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var json = await GetStringAsync(url, ct);
        return Json.Deserialize<T>(json);
    }

    public async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Json.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {url} -> {(int)response.StatusCode}: {text}");
        return Json.Deserialize<T>(text)!;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, HttpCompletionOption completion, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));

                var response = await _http.GetAsync(url, completion, timeout.Token);
                if (response.IsSuccessStatusCode) return response;

                if ((int)response.StatusCode is 404 or 403 or 410)
                {
                    response.Dispose();
                    throw new HttpRequestException($"Файл не найден ({(int)response.StatusCode}): {url}");
                }

                response.Dispose();
                last = new HttpRequestException($"HTTP {(int)response.StatusCode} для {url}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
            }

            var delay = TimeSpan.FromSeconds(attempt * 1.5);
            LogService.Default.Trace($"Повтор {attempt}/4 через {delay.TotalSeconds:0}с — {url}");
            await Task.Delay(delay, ct);
        }

        throw last ?? new HttpRequestException($"Не удалось скачать {url}");
    }

    /// <summary>Скачивание файла с проверкой SHA-1 и докачкой (.part).</summary>
    public async Task DownloadFileAsync(string url, string destination, string? sha1, long expectedSize,
        IProgress<DownloadStatus>? progress, CancellationToken ct, string phase = "Загрузка")
    {
        if (File.Exists(destination))
        {
            if (sha1 is null && expectedSize <= 0) return;
            if (expectedSize > 0 && new FileInfo(destination).Length == expectedSize && sha1 is null) return;
            if (sha1 is not null && Sha1(destination) == sha1) return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tempPath = destination + ".part";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                if (existing > 0 && expectedSize > 0 && existing >= expectedSize) existing = 0;

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                    LogService.Default.Trace($"Докачка {Path.GetFileName(destination)} с {existing}");
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} для {url}");

                var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                var total = expectedSize > 0
                    ? expectedSize
                    : (response.Content.Headers.ContentLength ?? 0) + existing;

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long received = existing;
                var sw = Stopwatch.StartNew();
                double lastTick = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress?.Report(new DownloadStatus(Path.GetFileName(destination), phase, total, received));

                    var speed = received / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                    if (sw.Elapsed.TotalSeconds - lastTick > 0.25)
                    {
                        lastTick = sw.Elapsed.TotalSeconds;
                        LogService.Default.Trace($"{Path.GetFileName(destination)} — {DownloadStatus.Format(received)} ({DownloadStatus.Format((long)speed)}/с)");
                    }
                }

                output.Flush();
                break;
            }
            catch (Exception ex) when (attempt < 4 && ex is not OperationCanceledException)
            {
                LogService.Default.Warn($"Ошибка скачивания {Path.GetFileName(destination)} (попытка {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        if (sha1 is not null && Sha1(tempPath) != sha1)
        {
            File.Delete(tempPath);
            throw new IOException($"Контрольная сумма не совпала: {Path.GetFileName(destination)}");
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(tempPath, destination);
    }

    public static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Обёртка над System.Text.Json c настройками по умолчанию.</summary>
public static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T? Deserialize<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Options);

    public static T? ReadFile<T>(string path) =>
        File.Exists(path) ? Deserialize<T>(File.ReadAllText(path)) : default;

    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(value));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}