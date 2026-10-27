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
            _ => (right, top)
        };

        MoveTo(targetLeft, targetTop);
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

        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag errors caused by rapid clicks.
        }
    }

    private void OnHideClicked(object sender, RoutedEventArgs e)
    {
        Hide();
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
        var shift = placement switch
        {
            FloatingHudPlacement.BottomLeft or FloatingHudPlacement.BottomCenter or FloatingHudPlacement.BottomRight => delta,
            FloatingHudPlacement.MiddleLeft or FloatingHudPlacement.MiddleRight => delta / 2,
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

        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
