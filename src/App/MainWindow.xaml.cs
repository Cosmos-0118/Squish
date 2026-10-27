using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using Squish.App.Infrastructure;
using Squish.App.Services;
using Squish.App.ViewModels;

namespace Squish.App;

public partial class MainWindow : Window
{
    private readonly KeyboardShortcutService _shortcutService;
    private readonly MainWindowViewModel _viewModel;
    private static readonly Uri WindowIconUri = new("pack://application:,,,/Squish;component/Assets/Brand/Squish.ico", UriKind.RelativeOrAbsolute);

    public MainWindow(MainWindowViewModel viewModel, KeyboardShortcutService shortcutService)
    {
        InitializeComponent();
        FitToWorkArea();
        ApplyWindowIcon();
        DataContext = viewModel;
        _viewModel = viewModel;
        ShortcutCatalog.ApplyMainWindowBindings(InputBindings, viewModel);
        _shortcutService = shortcutService;
        Loaded += OnLoaded;
        SizeChanged += (_, e) => _viewModel.UpdateLayoutWidth(e.NewSize.Width);
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        ThemeManager.ThemeApplied += OnThemeApplied;
        Closed += (_, _) => ThemeManager.ThemeApplied -= OnThemeApplied;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _shortcutService.Attach(this);
    }

    private void OnThemeApplied(object? sender, EventArgs e) => ApplyTitleBarTheme();

    /// <summary>
    /// Opens at a share of the screen instead of a fixed size: roomy on large monitors, never larger than a small
    /// laptop screen, and centred on the primary work area.
    /// </summary>
    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(workArea.Width, Math.Clamp(Math.Round(workArea.Width * 0.72), MinWidth, 1440));
        Height = Math.Min(workArea.Height, Math.Clamp(Math.Round(workArea.Height * 0.8), MinHeight, 960));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    /// <summary>Uses the dark Windows title bar whenever the active theme has a dark window background.</summary>
    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var isDark = TryFindResource("Brush.WindowBackground") is SolidColorBrush brush
            && (0.2126 * brush.Color.R + 0.7152 * brush.Color.G + 0.0722 * brush.Color.B) < 128;
        var value = new[] { isDark ? 1 : 0 };
        _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, value, sizeof(int));
    }

    private void ApplyWindowIcon()
    {
        try
        {
            StreamResourceInfo? resource = System.Windows.Application.GetResourceStream(WindowIconUri);
            if (resource?.Stream is null)
            {
                return;
            }

            using var icon = new Icon(resource.Stream);
            Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception)
        {
            // Ignore failures and fall back to default window icon.
        }
    }

    private static class NativeMethods
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
    }
}
