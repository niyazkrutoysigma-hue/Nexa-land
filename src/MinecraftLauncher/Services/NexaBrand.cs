using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Common;

namespace MinecraftLauncher.Services;

/// <summary>
/// Фирменный стиль NEXA LAND: генерация логотипа-ромбика (иконка приложения, логотип в меню).
/// Всё рисуется кодом, внешние ассеты не нужны.
/// </summary>
public static class NexaBrand
{
    /// <summary>Основные цвета бренда.</summary>
    public static readonly Color Primary = Color.FromRgb(0x8B, 0x5C, 0xF6);
    public static readonly Color PrimaryBright = Color.FromRgb(0xC0, 0x84, 0xFC);
    public static readonly Color Deep = Color.FromRgb(0x2A, 0x1B, 0x4D);
    public static readonly Color Dark = Color.FromRgb(0x14, 0x0B, 0x26);
    public static readonly Color Ink = Color.FromRgb(0x0A, 0x05, 0x14);

    public static string Title => "NEXA LAND";
    public static string Tagline => "сборки · моды · аккаунты";

    /// <summary>Рисует ромбик с градиентом и бликом, сохраняет в PNG.</summary>
    public static void RenderLogo(string path, int size = 512)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // мягкое свечение под ромбиком
                var glow = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.62,
                    RadiusY = 0.62
                };
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(120, Primary.R, Primary.G, Primary.B), 0));
                glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, Primary.R, Primary.G, Primary.B), 1));
                glow.Freeze();
                dc.DrawEllipse(glow, null, new Point(size / 2.0, size / 2.0), size * 0.48, size * 0.48);

                var s = size / 512.0;
                var half = size / 2.0;

                // верхняя грань (светлее)
                var top = new LinearGradientBrush(PrimaryBright, Primary, new Point(0, 0), new Point(1, 1));
                top.Freeze();
                dc.DrawGeometry(top, null, new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(half, 40 * s),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(half + 130 * s, half) },
                                new LineSegment { Point = new Point(half, half + 130 * s) },
                                new LineSegment { Point = new Point(half - 130 * s, half) }
                            }
                        }
                    }
                });

                // левая грань (темнее)
                var left = new LinearGradientBrush(Primary, Deep, new Point(0, 0), new Point(1, 1));
                left.Freeze();
                dc.DrawGeometry(left, null, new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(half - 130 * s, half),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(half, half + 130 * s) },
                                new LineSegment { Point = new Point(half, size - 40 * s) },
                                new LineSegment { Point = new Point(half - 130 * s, half - 0) }
                            }
                        }
                    }
                });

                // правая грань (самая тёмная)
                var right = new LinearGradientBrush(Deep, Ink, new Point(0, 0), new Point(1, 1));
                right.Freeze();
                dc.DrawGeometry(right, null, new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(half + 130 * s, half),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(half, half + 130 * s) },
                                new LineSegment { Point = new Point(half, size - 40 * s) }
                            }
                        }
                    }
                });

                // внутренний ромбик-искра
                var inner = new LinearGradientBrush(
                    Color.FromArgb(230, 255, 255, 255), Color.FromArgb(10, 255, 255, 255),
                    new Point(0, 0), new Point(0.6, 1));
                inner.Freeze();
                dc.DrawGeometry(inner, null, new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(half, half - 62 * s),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(half + 62 * s, half) },
                                new LineSegment { Point = new Point(half, half + 62 * s) },
                                new LineSegment { Point = new Point(half - 62 * s, half) }
                            }
                        }
                    }
                });

                // верхняя фаска-блик
                var shine = new LinearGradientBrush(
                    Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                    new Point(0, 0), new Point(0, 1));
                shine.Freeze();
                dc.DrawGeometry(shine, null, new PathGeometry
                {
                    Figures =
                    {
                        new PathFigure
                        {
                            StartPoint = new Point(half, half - 104 * s),
                            IsClosed = true,
                            Segments =
                            {
                                new LineSegment { Point = new Point(half + 96 * s, half - 10 * s) },
                                new LineSegment { Point = new Point(half, half - 18 * s) },
                                new LineSegment { Point = new Point(half - 96 * s, half - 10 * s) }
                            }
                        }
                    }
                });
            }

            Save(visual, size, size, path);
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Логотип не создан: {ex.Message}");
        }
    }

    /// <summary>Маленький логотип для меню (без свечения, резкие грани).</summary>
    public static void RenderMenuLogo(string path, int size = 96)
    {
        RenderLogo(path, size);
    }

    private static void Save(DrawingVisual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Создаёт иконку приложения — фирменный ромбик NEXA LAND.</summary>
    public static void EnsureIcon()
    {
        RenderLogo(AppPaths.LogoFile, 256);
    }

    /// <summary>Загруженная картинка логотипа для XAML (иначе null).</summary>
    public static ImageSource? LogoImage()
    {
        try
        {
            if (!File.Exists(AppPaths.LogoFile)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(AppPaths.LogoFile);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Случайный цвет акцента для карточек сборок (в пределах фиолетовой гаммы).</summary>
    public static Color AccentFor(string seed)
    {
        var palette = new[]
        {
            Color.FromRgb(0x8B, 0x5C, 0xF6),
            Color.FromRgb(0xA7, 0x6B, 0xFF),
            Color.FromRgb(0x6D, 0x8B, 0xFF),
            Color.FromRgb(0xC0, 0x4B, 0xF7),
            Color.FromRgb(0x5C, 0x6C, 0xF6),
            Color.FromRgb(0x9B, 0x5C, 0xF6)
        };

        var hash = 0;
        foreach (var c in seed) hash = hash * 31 + c;
        return palette[Math.Abs(hash) % palette.Length];
    }
}