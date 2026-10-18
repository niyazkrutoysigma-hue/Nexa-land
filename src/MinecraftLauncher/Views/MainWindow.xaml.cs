using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Common;
using MinecraftLauncher.Services;
using MinecraftLauncher.ViewModels;

namespace MinecraftLauncher.Views;

public partial class MainWindow : Window
{
    private IDisposable? _toastSubscription;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnWindowLoaded;
        DataContext = new MainViewModel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableDarkTitleBar();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        ApplyWindowIcon();

        if (MainViewModel.Current is { } main)
        {
            _toastSubscription = ToastService.Default.Subscribe(main.PushToast);
        }
    }

    /// <summary>Иконка ромбика в заголовке окна и в списке задач.</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            NexaBrand.EnsureIcon();
            if (!File.Exists(AppPaths.LogoFile)) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(AppPaths.LogoFile);
            image.EndInit();
            image.Freeze();

            Icon = image;
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Иконка окна не установлена: {ex.Message}");
        }
    }

    /// <summary>
    /// Подгоняем окно под рабочую область монитора в логических единицах.
    /// SystemParameters.WorkArea при 150% DPI отдаёт непредсказуемые значения,
    /// поэтому берём GetMonitorInfo (пиксели) и делим на масштаб окна.
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var workWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
            var workHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;

            MinWidth = Math.Min(920, Math.Max(620, workWidth - 16));
            MinHeight = Math.Min(620, Math.Max(460, workHeight - 16));

            var width = Math.Min(1280, workWidth - 24);
            var height = Math.Min(820, workHeight - 24);

            if (Math.Abs(Width - width) > 1) Width = width;
            if (Math.Abs(Height - height) > 1) Height = height;
        }
        catch (Exception ex)
        {
            LogService.Default.Trace($"Не удалось подогнать окно: {ex.Message}");
        }
    }

    private void OnToastCloseClick(object sender, RoutedEventArgs e)
    {
        if (MainViewModel.Current is { } main && (sender as FrameworkElement)?.Tag is Toast toast)
            main.DismissToast(toast);
    }

    private void OnToastActionClick(object sender, RoutedEventArgs e)
    {
        if (MainViewModel.Current is { } main && (sender as FrameworkElement)?.Tag is Toast toast)
        {
            main.DismissToast(toast);
            toast.Action?.Invoke();
        }
    }

    /// <summary>Тёмный заголовок окна в Windows 10/11 (DWM).</summary>
    private void EnableDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var useDark = 1;
            // 20 — актуальный атрибут (Win10 2004+/Win11), 19 — для старых сборок
            if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
        }
        catch (Exception)
        {
            // оформление заголовка не критично
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}