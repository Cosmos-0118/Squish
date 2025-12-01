using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using ClopWindows.App.ViewModels;

namespace ClopWindows.App.Views.FloatingHud;

public partial class FloatingHudWindow : Window
{
    private const double MarginFromScreen = 24d;

    public FloatingHudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkModeHint();
    }

    private FloatingHudViewModel? ViewModel => DataContext as FloatingHudViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsPinned)
        {
            MoveToTopRight();
        }
    }

    public void MoveToTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        Left = workArea.Right - width - MarginFromScreen;
        Top = workArea.Top + MarginFromScreen;
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
