using System.Security.Cryptography;
using System.Text;

namespace MinecraftLauncher.Common;

/// <summary>
/// Шифрование файлов средствами DPAPI (ProtectedData): токены Minecraft не лежат на диске
/// в открытом виде и привязаны к учётной записи Windows пользователя.
/// </summary>
public static class SecureStore
{
    private const string Prefix = "dpapi:";

    public static void Save(string path, string plaintext)
    {
        var protectedData = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);

        var temp = path + ".tmp";
        File.WriteAllText(temp, Prefix + Convert.ToBase64String(protectedData));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static string? Load(string path)
    {
        if (!File.Exists(path)) return null;

        string raw;
        try { raw = File.ReadAllText(path).Trim(); }
        catch (Exception ex)
        {
            LogService.Default.Warn($"Не удалось прочитать защищённый файл: {ex.Message}");
            return null;
        }

        if (raw.Length == 0) return null;

        // файл без префикса — старый формат, читаем как есть
        if (!raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return raw;

        byte[] data;
        try { data = Convert.FromBase64String(raw[Prefix.Length..]); }
        catch (FormatException)
        {
            LogService.Default.Warn("Повреждённый защищённый файл");
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            LogService.Default.Warn($"Не удалось расшифровать файл (файл создан другой учётной записью?): {ex.Message}");
            return null;
        }
    }
}