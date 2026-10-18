using System.Text.Json.Serialization;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>Хранилище аккаунтов: файл шифруется DPAPI.</summary>
public sealed class AccountService : ObservableObject
{
    private readonly List<Account> _accounts = [];
    private Account? _active;

    public IReadOnlyList<Account> Accounts => _accounts;

    public Account? Active
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            foreach (var account in _accounts) account.IsActive = ReferenceEquals(account, value);
            _active = value;
            OnPropertyChanged();
            Save();
        }
    }

    public void Load()
    {
        _accounts.Clear();

        try
        {
            if (!File.Exists(AppPaths.AccountsFile))
            {
                LogService.Default.Info("Файл аккаунтов отсутствует — начинаем с чистого листа");
                return;
            }

            var json = SecureStore.Load(AppPaths.AccountsFile);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = Json.Deserialize<List<Account>>(json);
                if (loaded is not null) _accounts.AddRange(loaded);
            }
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Не удалось прочитать аккаунты", ex);
        }

        _active = _accounts.FirstOrDefault(a => a.IsActive) ?? _accounts.FirstOrDefault();
        if (_active is not null)
        {
            foreach (var a in _accounts) a.IsActive = ReferenceEquals(a, _active);
        }

        LogService.Default.Info($"Аккаунтов: {_accounts.Count}");
    }

    public void Save()
    {
        try
        {
            SecureStore.Save(AppPaths.AccountsFile, Json.Serialize(_accounts));
        }
        catch (Exception ex)
        {
            LogService.Default.Error("Не удалось сохранить аккаунты", ex);
        }
    }

    public void Add(Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.DisplayName))
        {
            account.DisplayName = account.DisplayName.Trim();
            if (_accounts.Count == 0) Active = account;
        }

        _accounts.Add(account);
        if (Active is null) Active = account;
        Save();
    }

    public void Remove(Account account)
    {
        _accounts.Remove(account);
        if (ReferenceEquals(_active, account)) _active = _accounts.FirstOrDefault();
        Save();
    }

    public void Update(Account account) => Save();

    public void UpdateTokens(Account account, string accessToken, string? refreshToken, string? xuid)
    {
        account.AccessToken = accessToken;
        if (!string.IsNullOrEmpty(refreshToken)) account.RefreshToken = refreshToken;
        if (!string.IsNullOrEmpty(xuid)) account.Xuid = xuid;
        account.LastError = null;
        Save();
    }

    public void SetError(Account account, string error)
    {
        account.LastError = error;
        Save();
    }
}

/// <summary>Авторизация Microsoft по device code flow (без встроенного окна входа).</summary>
public sealed class MicrosoftAuthService
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftTokenUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    private readonly ApiClient _api;

    public MicrosoftAuthService(ApiClient api) => _api = api;

    public sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    }

    public sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    public sealed class XboxTokenResponse
    {
        [JsonPropertyName("Token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("DisplayClaims")] public List<XboxDisplayClaim> DisplayClaims { get; set; } = [];
    }

    public sealed class XboxDisplayClaim
    {
        [JsonPropertyName("xui")] public List<XboxClaim> Xui { get; set; } = [];
    }

    public sealed class XboxClaim
    {
        [JsonPropertyName("uhs")] public string Uhs { get; set; } = string.Empty;
        [JsonPropertyName("xid")] public string Xid { get; set; } = string.Empty;
    }

    public sealed class McProfile
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("skins")] public List<McSkin> Skins { get; set; } = [];
    }

    public sealed class McSkin
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("variant")] public string? Variant { get; set; }
    }

    public async Task<DeviceCodeResponse> StartDeviceCodeAsync(string clientId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "service::user.auth.xboxlive.com::MBI_SSL offline_access"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(body)
        };

        using var response = await _api.Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException(text);

        return Json.Deserialize<DeviceCodeResponse>(text)
               ?? throw new IOException("Пустой ответ device code");
    }

    /// <summary>Ожидание подтверждения. Возвращает MSA-токен либо null при таймауте.</summary>
    public async Task<TokenResponse?> WaitForTokenAsync(string clientId, string deviceCode, int intervalSeconds,
        IProgress<string>? status, Action<string>? onUserCode, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(300);
        var interval = Math.Max(intervalSeconds, 2);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            status?.Report($"Ожидание подтверждения в браузере… (код {deviceCode})");
            onUserCode?.Invoke(deviceCode);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };

            using var response = await _api.Http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return Json.Deserialize<TokenResponse>(text);

            var error = Json.Deserialize<TokenResponse>(text) is null ? text : text;
            if (text.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (text.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += 5;
                continue;
            }

            throw new IOException("Ошибка авторизации: " + ExtractError(text));
        }

        return null;
    }

    public async Task<Account> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = "service::user.auth.xboxlive.com::MBI_SSL offline_access"
            })
        };

        using var response = await _api.Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException("Не удалось обновить токен: " + text);

        var token = Json.Deserialize<TokenResponse>(text) ?? throw new IOException("Пустой ответ токена");
        var account = await CompleteLoginAsync(token, ct)
            ?? throw new IOException("Не удалось обновить профиль");
        account.RefreshToken ??= refreshToken;
        account.MsaClientId ??= clientId;
        return account;
    }

    /// <summary>Полный путь: MSA -> XBL -> XSTS -> Minecraft.</summary>
    public async Task<Account?> CompleteLoginAsync(TokenResponse token, CancellationToken ct = default)
    {
        // 1. Xbox Live
        var xblRequest = new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, string>
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + token.AccessToken
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };

        var xbl = await _api.PostJsonAsync<XboxTokenResponse>(XblUrl, xblRequest, ct);
        var xui = xbl.DisplayClaims.FirstOrDefault()?.Xui.FirstOrDefault()
                  ?? throw new IOException("XBL не вернул XUID");

        // 2. XSTS
        var xstsRequest = new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, string>
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = xbl.Token
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        };

        string xstsToken;
        try
        {
            var xsts = await _api.PostJsonAsync<XboxTokenResponse>(XstsUrl, xstsRequest, ct);
            xstsToken = xsts.Token;
        }
        catch (Exception)
        {
            throw new UnauthorizedAccessException(
                "Аккаунт без Xbox Live. Нужен Minecraft: Java Edition или Game Pass.");
        }

        // 3. Право владения
        using var entRequest = new HttpRequestMessage(HttpMethod.Get, EntitlementsUrl);
        entRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", xstsToken);
        using var entResponse = await _api.Http.SendAsync(entRequest, ct);
        var entitlements = await entResponse.Content.ReadAsStringAsync(ct);
        if (!entitlements.Contains("product_minecraft", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("На этом аккаунте нет Minecraft: Java Edition.");

        // 4. Minecraft login
        var mcLogin = await _api.PostJsonAsync<JsonElement>(MinecraftTokenUrl,
            new Dictionary<string, string> { ["identityToken"] = $"XBL3.0 x={xui.Uhs};{xui.Xid}" }, ct);

        var accessToken = mcLogin.GetProperty("access_token").GetString()
                          ?? throw new IOException("Minecraft не вернул access_token");
        var expires = mcLogin.TryGetProperty("expires_in", out var exp) ? exp.GetInt64() : 86400;

        // 5. Профиль
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var profileResponse = await _api.Http.SendAsync(profileRequest, ct);
        var profileText = await profileResponse.Content.ReadAsStringAsync(ct);
        if (!profileResponse.IsSuccessStatusCode)
            throw new IOException("Профиль не найден: " + ExtractError(profileText));

        var profile = Json.Deserialize<McProfile>(profileText) ?? throw new IOException("Пустой профиль");
        var avatar = profile.Skins.FirstOrDefault(k => k.State == "ACTIVE")?.Url
                     ?? profile.Skins.FirstOrDefault()?.Url;

        LogService.Default.Success($"Авторизация успешна: {profile.Name}");

        return new Account
        {
            Type = "Microsoft",
            DisplayName = profile.Name,
            Uuid = profile.Id,
            AccessToken = accessToken,
            RefreshToken = token.RefreshToken,
            Xuid = xui.Xid,
            AvatarUrl = avatar,
            MsaClientId = SettingsService.Default.MsaClientId
        };
    }

    private static string ExtractError(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_description", out var desc)) return desc.GetString() ?? json;
            if (doc.RootElement.TryGetProperty("error", out var err)) return err.GetString() ?? json;
        }
        catch { }
        return json.Length > 400 ? json[..400] : json;
    }
}