using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClopWindows.App.ViewModels;

namespace ClopWindows.App.Views.FloatingHud;

public partial class FloatingHudWindow : Window
{
    private const double MarginFromScreen = 24d;
    public static readonly DependencyProperty IsPlacementModeProperty = DependencyProperty.Register(
        nameof(IsPlacementMode), typeof(bool), typeof(FloatingHudWindow), new PropertyMetadata(false));

    public event EventHandler? PlacementConfirmed;
    public event EventHandler? PlacementCancelled;

    public bool IsPlacementMode
    {
        get => (bool)GetValue(IsPlacementModeProperty);
        set => SetValue(IsPlacementModeProperty, value);
    }

    public bool IsPinnedPosition { get; private set; }

    public FloatingHudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTransparentBackground();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!IsPinnedPosition)
        {
            MoveToTopRight();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsPinnedPosition && !IsPlacementMode)
        {
            MoveToTopRight();
        }
    }

    public void MoveToTopRight()
    {
        if (IsPlacementMode)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        Left = workArea.Right - width - MarginFromScreen;
        Top = workArea.Top + MarginFromScreen;
        IsPinnedPosition = false;
    }

    public void MoveTo(double left, double top)
    {
        IsPinnedPosition = true;
        Left = left;
        Top = top;
    }

    public void ClearPinnedPosition()
    {
        IsPinnedPosition = false;
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

    private void ApplyTransparentBackground()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        var hwnd = source.Handle;
        var attrValue = new int[] { 2 }; // DWMWA_FORCE_ICONIC_REPRESENTATION off, ensures transparency works
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, attrValue, sizeof(int));
    }

    private static class NativeMethods
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);
    }

    public void EnterPlacementMode()
    {
        IsPlacementMode = true;
        BringToFront();
    }

    public void ExitPlacementMode()
    {
        IsPlacementMode = false;
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

    private void OnHudMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsPlacementMode)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore drag errors caused by rapid clicks.
            }
        }
    }

    private void OnPlacementConfirmClicked(object sender, RoutedEventArgs e)
    {
        PlacementConfirmed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlacementCancelClicked(object sender, RoutedEventArgs e)
    {
        PlacementCancelled?.Invoke(this, EventArgs.Empty);
    }
}
