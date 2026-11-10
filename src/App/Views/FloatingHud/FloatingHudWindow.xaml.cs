using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using Squish.App.ViewModels;
using Squish.Core.Settings;

namespace Squish.App.Views.FloatingHud;

public partial class FloatingHudWindow : Window
{
    private const double MarginFromScreen = 24d;
    private const int WM_SIZING = 0x0214;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private System.Windows.Point? _dragStartPoint;
    private FrameworkElement? _dragSource;
    private bool _isDraggingCard;
    private bool _isUserResizing;
    private bool _isUserMoving;
    private bool _hasRoundedCorners;

    public FloatingHudWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnSizeChanged;
        Closed += (_, _) => Squish.App.Services.ThemeManager.ThemeApplied -= OnThemeApplied;
        Squish.App.Services.ThemeManager.ThemeApplied += OnThemeApplied;
    }

    public void MoveToPlacement(FloatingHudPlacement placement)
    {
        // A progress update must not yank the window away from under the cursor mid-drag.
        if (_isUserMoving)
        {
            return;
        }

        if (placement == FloatingHudPlacement.Custom)
        {
            if (TryMoveToCustomPosition())
            {
                return;
            }

            // Never dragged yet, or the monitor it was on is gone: start in the middle, where it is easy to grab.
            placement = FloatingHudPlacement.Center;
        }

        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        if (width <= 0)
        {
            width = Math.Max(Width, MinWidth);
        }

        if (height <= 0)
        {
            height = Math.Max(Height, MinHeight);
        }

        var horizontalMin = workArea.Left;
        var horizontalMax = workArea.Right - width;
        var verticalMin = workArea.Top;
        var verticalMax = workArea.Bottom - height;

        var left = Math.Clamp(workArea.Left + MarginFromScreen, horizontalMin, horizontalMax);
        var right = Math.Clamp(workArea.Right - width - MarginFromScreen, horizontalMin, horizontalMax);
        var centerX = Math.Clamp(workArea.Left + (workArea.Width - width) / 2, horizontalMin, horizontalMax);

        var top = Math.Clamp(workArea.Top + MarginFromScreen, verticalMin, verticalMax);
        var bottom = Math.Clamp(workArea.Bottom - height - MarginFromScreen, verticalMin, verticalMax);
        var centerY = Math.Clamp(workArea.Top + (workArea.Height - height) / 2, verticalMin, verticalMax);

        var (targetLeft, targetTop) = placement switch
        {
            FloatingHudPlacement.TopLeft => (left, top),
            FloatingHudPlacement.TopCenter => (centerX, top),
            FloatingHudPlacement.TopRight => (right, top),
            FloatingHudPlacement.MiddleLeft => (left, centerY),
            FloatingHudPlacement.MiddleRight => (right, centerY),
            FloatingHudPlacement.BottomLeft => (left, bottom),
            FloatingHudPlacement.BottomCenter => (centerX, bottom),
            FloatingHudPlacement.BottomRight => (right, bottom),
            FloatingHudPlacement.Center => (centerX, centerY),
            _ => (right, top)
        };

        MoveTo(targetLeft, targetTop);
    }

    private bool TryMoveToCustomPosition()
    {
        var left = SettingsHost.Get(SettingsRegistry.FloatingHudCustomLeft);
        var top = SettingsHost.Get(SettingsRegistry.FloatingHudCustomTop);
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return false;
        }

        MoveTo(left, top);
        if (!IsTitleBarOnScreen())
        {
            return false;
        }

        KeepBottomOnScreen();
        return true;
    }

    public void MoveTo(double left, double top)
    {
        Left = left;
        Top = top;
    }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        Topmost = false;
        Topmost = true;
    }

    public bool TryAnimateDismissal(FloatingResultViewModel viewModel, Action onCompleted)
    {
        if (viewModel is null)
        {
            return false;
        }

        if (ResultItemsControl is null || !ResultItemsControl.Items.Contains(viewModel))
        {
            return false;
        }

        var card = GetResultCard(viewModel);
        if (card is null)
        {
            return false;
        }

        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        animation.Completed += (_, _) =>
        {
            // Drop the held clock (and the closure it keeps alive) before restoring the local value.
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            onCompleted();
        };

        card.BeginAnimation(OpacityProperty, animation);
        return true;
    }

    private FrameworkElement? GetResultCard(FloatingResultViewModel viewModel)
    {
        if (ResultItemsControl is null)
        {
            return null;
        }

        var container = ResultItemsControl.ItemContainerGenerator.ContainerFromItem(viewModel) as FrameworkElement;
        if (container is null)
        {
            ResultItemsControl.UpdateLayout();
            container = ResultItemsControl.ItemContainerGenerator.ContainerFromItem(viewModel) as FrameworkElement;
        }

        if (container is null)
        {
            return null;
        }

        container.ApplyTemplate();
        return FindDescendantByName(container, "ResultCard");
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
        {
            return fe;
        }

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is null)
            {
                continue;
            }

            if (FindDescendantByName(child, name) is FrameworkElement match)
            {
                return match;
            }
        }

        return null;
    }

    private void OnResultCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveChild(e.OriginalSource))
        {
            ResetDragState();
            return;
        }

        _dragStartPoint = e.GetPosition(this);
        _dragSource = sender as FrameworkElement;
    }

    private void OnResultCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStartPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!ReferenceEquals(sender, _dragSource))
        {
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - _dragStartPoint.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!TryGetResultViewModel(sender, out var viewModel) || !viewModel.IsSuccess)
        {
            ResetDragState();
            return;
        }

        if (!viewModel.TryGetResolvedFilePath(out var filePath))
        {
            ResetDragState();
            return;
        }

        _isDraggingCard = true;
        try
        {
            var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { filePath! });
            System.Windows.DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        }
        finally
        {
            _isDraggingCard = false;
            ResetDragState();
        }
    }

    private void OnResultCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingCard || IsInteractiveChild(e.OriginalSource))
        {
            ResetDragState();
            return;
        }

        if (!TryGetResultViewModel(sender, out var viewModel))
        {
            ResetDragState();
            return;
        }

        if (viewModel.IsRunning || !viewModel.IsSuccess)
        {
            ResetDragState();
            return;
        }

        if (viewModel.TryGetResolvedFilePath(out var filePath))
        {
            OpenFileLocation(filePath!);
            e.Handled = true;
        }

        ResetDragState();
    }

    private void OnResultCardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetDragState();
        }
    }

    private static bool TryGetResultViewModel(object sender, out FloatingResultViewModel viewModel)
    {
        if (sender is FrameworkElement element && element.DataContext is FloatingResultViewModel vm)
        {
            viewModel = vm;
            return true;
        }

        viewModel = null!;
        return false;
    }

    private static bool IsInteractiveChild(object? source)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static void OpenFileLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // Swallow failures so the HUD remains stable even if Explorer cannot be opened.
        }
    }

    private void ResetDragState()
    {
        _dragStartPoint = null;
        _dragSource = null;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var startLeft = Left;
        var startTop = Top;
        _isUserMoving = true;
        try
        {
            // DragMove runs the system move loop and returns once the mouse button is released.
            DragMove();
        }
        catch
        {
            // Ignore drag errors caused by rapid clicks.
        }
        finally
        {
            _isUserMoving = false;
        }

        // A plain click on the header is not a move.
        if (Math.Abs(Left - startLeft) < 1 && Math.Abs(Top - startTop) < 1)
        {
            return;
        }

        // Dropped with the header off every screen: put it back, or it could never be grabbed again.
        if (!IsTitleBarOnScreen())
        {
            MoveTo(startLeft, startTop);
            return;
        }

        KeepBottomOnScreen();

        // Remember the spot and switch to Custom so later updates and restarts keep the HUD here.
        SettingsHost.Set(SettingsRegistry.FloatingHudCustomLeft, Left);
        SettingsHost.Set(SettingsRegistry.FloatingHudCustomTop, Top);
        SettingsHost.Set(SettingsRegistry.FloatingHudPlacement, FloatingHudPlacement.Custom);
    }

    /// <summary>
    /// True when the middle of the header (the drag handle) is on a connected monitor. Checked in device pixels
    /// against the real monitor layout, so it is right on multi-monitor setups with mixed scaling too.
    /// </summary>
    private bool IsTitleBarOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            // Not created yet; nothing to check against.
            return true;
        }

        var point = new NativeMethods.POINT
        {
            X = rect.Left + (rect.Right - rect.Left) / 2,
            Y = rect.Top + Math.Min(16, Math.Max(0, (rect.Bottom - rect.Top) / 2))
        };

        return NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>
    /// A custom-placed HUD keeps its top edge where the user put it and grows downwards. If it would run past the
    /// bottom of its monitor's work area, slide it up just enough (never above the work area's top).
    /// </summary>
    private void KeepBottomOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var overflow = rect.Bottom - info.rcWork.Bottom;
        if (overflow <= 0)
        {
            return;
        }

        var newTop = Math.Max(info.rcWork.Top, rect.Top - overflow);
        if (newTop == rect.Top)
        {
            return;
        }

        // Moving the HWND directly (in device pixels) lets WPF pick up the new Top itself, with no DPI conversion here.
        _ = NativeMethods.SetWindowPos(handle, IntPtr.Zero, rect.Left, newTop, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnHideClicked(object sender, RoutedEventArgs e)
    {
        // Close (not Hide) so the window and its visual tree are released; the controller recreates it on demand.
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        source.AddHook(WndProc);

        // Windows 11 rounds the corners and draws a 1px frame itself; older systems keep the WPF border.
        var preference = NativeMethods.DWMWCP_ROUND;
        _hasRoundedCorners = NativeMethods.DwmSetWindowAttribute(source.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0;
        if (_hasRoundedCorners)
        {
            RootBorder.BorderThickness = new Thickness(0);
        }

        ApplyFrameColours();
    }

    private void OnThemeApplied(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ApplyFrameColours);

    private void ApplyFrameColours()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        var isDark = TryFindResource("Brush.HudBackground") is SolidColorBrush background
            && (0.2126 * background.Color.R + 0.7152 * background.Color.G + 0.0722 * background.Color.B) < 128;
        var darkMode = isDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(source.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        if (_hasRoundedCorners && TryFindResource("Brush.HudCardBorder") is SolidColorBrush border)
        {
            var colorRef = border.Color.R | (border.Color.G << 8) | (border.Color.B << 16);
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, NativeMethods.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_SIZING:
                _isUserResizing = true;
                break;
            case WM_EXITSIZEMOVE when _isUserResizing:
                _isUserResizing = false;
                OnUserResized();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Dragging an edge switches WPF to manual sizing. Keep the new width, treat the new height as the HUD's
    /// maximum height, and go back to sizing the height to the list.
    /// </summary>
    private void OnUserResized()
    {
        if (DataContext is FloatingHudViewModel viewModel)
        {
            viewModel.WindowWidth = ActualWidth;
            viewModel.WindowHeight = ActualHeight;
        }

        SizeToContent = SizeToContent.Height;
    }

    /// <summary>
    /// The window grows downwards as rows arrive. Keep the edge it is pinned to in place so a HUD at the bottom of
    /// the screen grows upwards instead of running off it.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || _isUserResizing || e.PreviousSize.Height <= 0)
        {
            return;
        }

        var delta = e.NewSize.Height - e.PreviousSize.Height;
        var placement = SettingsHost.Get(SettingsRegistry.FloatingHudPlacement);
        if (placement == FloatingHudPlacement.Custom)
        {
            // Keep the user's spot; only nudge up if the taller window would run off the bottom of its monitor.
            // Deferred so the HWND already has its new size when it is measured.
            if (!_isUserMoving)
            {
                _ = Dispatcher.InvokeAsync(KeepBottomOnScreen, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            return;
        }

        var shift = placement switch
        {
            FloatingHudPlacement.BottomLeft or FloatingHudPlacement.BottomCenter or FloatingHudPlacement.BottomRight => delta,
            FloatingHudPlacement.MiddleLeft or FloatingHudPlacement.MiddleRight or FloatingHudPlacement.Center => delta / 2,
            _ => 0d
        };

        if (shift != 0)
        {
            var workArea = SystemParameters.WorkArea;
            Top = Math.Clamp(Top - shift, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - e.NewSize.Height));
        }
    }

    private static class NativeMethods
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;
        public const uint MONITOR_DEFAULTTONULL = 0;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
