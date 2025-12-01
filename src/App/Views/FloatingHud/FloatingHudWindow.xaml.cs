using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using ClopWindows.App.ViewModels;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.Views.FloatingHud;

public partial class FloatingHudWindow : Window
{
    private const double MarginFromScreen = 24d;

    public FloatingHudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkModeHint();
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

        if (ResultsItemsControl.ItemContainerGenerator.ContainerFromItem(viewModel) is not ContentPresenter presenter)
        {
            return false;
        }

        if (presenter.ContentTemplate?.FindName("ResultCard", presenter) is not FrameworkElement card)
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

    private void ApplyDarkModeHint()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        var hwnd = source.Handle;
        var attrValue = new[] { 1 };
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, attrValue, sizeof(int));
    }

    private static class NativeMethods
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
    }
}
