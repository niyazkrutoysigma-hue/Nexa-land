using System.Windows;
using System.Windows.Media;

namespace MinecraftLauncher.Services;

/// <summary>
/// Строит палитру кистей из темы и акцентного цвета и раскладывает её в ресурсы приложения.
/// Фиолетовая гамма NEXA LAND: тёмная тема — глубокий индиго-фиолетовый.
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager Default { get; } = new();

    private ThemeManager() { }

    public void Apply(AppTheme theme, string accentHex)
    {
        var resources = Application.Current.Resources;
        var accent = ParseColor(accentHex, NexaBrand.Primary);
        var dark = theme == AppTheme.Dark;

        // Фон: глубокий фиолетовый вместо нейтрального серого
        Set(resources, "Bg.Base", dark ? Color.FromRgb(0x0C, 0x08, 0x18) : Color.FromRgb(0xF4, 0xF1, 0xFA));
        Set(resources, "Bg.Elev1", dark ? Color.FromRgb(0x15, 0x10, 0x28) : Color.FromRgb(0xFC, 0xFA, 0xFF));
        Set(resources, "Bg.Elev2", dark ? Color.FromRgb(0x1E, 0x17, 0x38) : Color.FromRgb(0xEA, 0xE5, 0xF6));
        Set(resources, "Bg.Sunken", dark ? Color.FromRgb(0x08, 0x05, 0x12) : Color.FromRgb(0xE4, 0xDE, 0xF2));
        Set(resources, "Bg.Hover", dark
            ? Color.FromArgb((byte)0x22, (byte)0xC0, (byte)0x84, (byte)0xFC)
            : Color.FromArgb((byte)0x14, (byte)0x5C, (byte)0x3A, (byte)0x8A));

        // Стеклянные панели с лёгким фиолетовым подсветом
        Set(resources, "Bg.Glass", dark
            ? Color.FromArgb((byte)0xCC, (byte)0x18, (byte)0x12, (byte)0x2E)
            : Colors.White);

        // Текст
        Set(resources, "Fg.Primary", dark ? Color.FromRgb(0xF3, 0xEF, 0xFF) : Color.FromRgb(0x14, 0x0D, 0x24));
        Set(resources, "Fg.Secondary", dark ? Color.FromRgb(0xB3, 0xA8, 0xD1) : Color.FromRgb(0x4B, 0x42, 0x5E));
        Set(resources, "Fg.Muted", dark ? Color.FromRgb(0x7A, 0x6F, 0x99) : Color.FromRgb(0x8A, 0x81, 0x9E));
        Set(resources, "Fg.OnAccent", Colors.White);
        Set(resources, "Fg.Glow", dark
            ? Color.FromArgb((byte)0x66, (byte)0xC0, (byte)0x84, (byte)0xFC)
            : Color.FromArgb((byte)0x33, (byte)0x5C, (byte)0x3A, (byte)0x8A));

        // Границы
        Set(resources, "Stroke.Default", dark ? Color.FromRgb(0x2A, 0x20, 0x45) : Color.FromRgb(0xDC, 0xD4, 0xEE));
        Set(resources, "Stroke.Strong", dark ? Color.FromRgb(0x3D, 0x2E, 0x66) : Color.FromRgb(0xBF, 0xB2, 0xDD));

        // Акцент
        Set(resources, "Accent.Base", accent);
        Set(resources, "Accent.Hover", Shade(accent, 0.14));
        Set(resources, "Accent.Press", Shade(accent, 0.24));
        Set(resources, "Accent.Soft", WithAlpha(accent, dark ? 0.20 : 0.14));
        Set(resources, "Accent.Border", WithAlpha(accent, 0.60));
        Set(resources, "Accent.Deep", Shade(accent, -0.34));

        // Статусы
        Set(resources, "Status.Ok", dark ? Color.FromRgb(0x5E, 0xE0, 0xA8) : Color.FromRgb(0x1B, 0x9C, 0x57));
        Set(resources, "Status.Warn", dark ? Color.FromRgb(0xF5, 0xC4, 0x6B) : Color.FromRgb(0xB5, 0x84, 0x00));
        Set(resources, "Status.Err", dark ? Color.FromRgb(0xFF, 0x7B, 0xA8) : Color.FromRgb(0xD1, 0x2F, 0x5C));

        Set(resources, "Theme.IsDark", dark);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void Set(ResourceDictionary resources, string key, bool value) => resources[key] = value;

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var value = hex.Trim().TrimStart('#');
            if (value.Length == 6) value += "FF";
            if (value.Length != 8) return fallback;
            return Color.FromArgb(
                byte.Parse(value[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(value.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Осветление цвета через HSL.</summary>
    public static Color Shade(Color color, double amount)
    {
        var (h, s, l) = RgbToHsl(color);
        l = Math.Clamp(l + amount, 0, 1);
        return HslToRgb(h, s, l);
    }

    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    public static Color Mix(Color first, Color second, double ratio) => Color.FromRgb(
        (byte)(first.R * (1 - ratio) + second.R * ratio),
        (byte)(first.G * (1 - ratio) + second.G * ratio),
        (byte)(first.B * (1 - ratio) + second.B * ratio));

    private static (double H, double S, double L) RgbToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var delta = max - min;

        if (delta == 0) return (0, 0, l);

        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == r) h = ((g - b) / delta + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / delta + 2) / 6;
        else h = ((r - g) / delta + 4) / 6;

        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s == 0)
        {
            var value = (byte)Math.Round(l * 255);
            return Color.FromRgb(value, value, value);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToChannel(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueToChannel(p, q, h) * 255),
            (byte)Math.Round(HueToChannel(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}