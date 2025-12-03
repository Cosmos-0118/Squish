using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClopWindows.App.Infrastructure;

/// <summary>
/// Provides simple helpers for the floating HUD layouts.
/// </summary>
public static class HudLayoutHelper
{
    public static readonly DependencyProperty FitToParentProperty = DependencyProperty.RegisterAttached(
        "FitToParent",
        typeof(bool),
        typeof(HudLayoutHelper),
        new PropertyMetadata(false, OnFitToParentChanged));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.RegisterAttached(
        "Padding",
        typeof(double),
        typeof(HudLayoutHelper),
        new PropertyMetadata(0d, OnPaddingChanged));

    private static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached(
        "Context",
        typeof(ResizeContext),
        typeof(HudLayoutHelper),
        new PropertyMetadata(null));

    public static void SetFitToParent(DependencyObject element, bool value) => element.SetValue(FitToParentProperty, value);

    public static bool GetFitToParent(DependencyObject element) => (bool)element.GetValue(FitToParentProperty);

    public static void SetPadding(DependencyObject element, double value) => element.SetValue(PaddingProperty, value);

    public static double GetPadding(DependencyObject element) => (double)element.GetValue(PaddingProperty);

    private static void OnFitToParentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.Loaded += OnElementLoaded;
            element.Unloaded += OnElementUnloaded;
            TryAttach(element);
        }
        else
        {
            element.Loaded -= OnElementLoaded;
            element.Unloaded -= OnElementUnloaded;
            Detach(element);
        }
    }

    private static void OnPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && element.GetValue(ContextProperty) is ResizeContext context)
        {
            context.Padding = (double)e.NewValue;
            context.Refresh();
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            TryAttach(element);
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Detach(element);
        }
    }

    private static void TryAttach(FrameworkElement element)
    {
        if (element.GetValue(ContextProperty) is ResizeContext)
        {
            return;
        }

        if (!element.IsLoaded)
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TryAttach(element)));
            return;
        }

        var parent = FindParent(element);
        if (parent is null)
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TryAttach(element)));
            return;
        }

        var context = new ResizeContext(element, parent)
        {
            Padding = GetPadding(element)
        };

        element.SetValue(ContextProperty, context);
        context.Attach();
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(ContextProperty) is ResizeContext context)
        {
            context.Detach();
            element.ClearValue(ContextProperty);
        }
    }

    private static FrameworkElement? FindParent(FrameworkElement element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is FrameworkElement fe)
            {
                return fe;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return element.Parent as FrameworkElement;
    }

    private sealed class ResizeContext
    {
        private readonly FrameworkElement _element;
        private readonly FrameworkElement _parent;
        private readonly object _originalMaxHeight;
        private double _lastApplied = double.NaN;
        private bool _queued;

        public ResizeContext(FrameworkElement element, FrameworkElement parent)
        {
            _element = element;
            _parent = parent;
            _originalMaxHeight = element.ReadLocalValue(FrameworkElement.MaxHeightProperty);
        }

        public double Padding { get; set; }

        public void Attach()
        {
            _parent.SizeChanged += OnParentSizeChanged;
            _element.SizeChanged += OnElementSizeChanged;
            _element.Unloaded += OnElementUnloaded;
            RequestUpdate();
        }

        public void Detach()
        {
            _parent.SizeChanged -= OnParentSizeChanged;
            _element.SizeChanged -= OnElementSizeChanged;
            _element.Unloaded -= OnElementUnloaded;

            if (_originalMaxHeight == DependencyProperty.UnsetValue)
            {
                _element.ClearValue(FrameworkElement.MaxHeightProperty);
            }
            else
            {
                _element.SetValue(FrameworkElement.MaxHeightProperty, _originalMaxHeight);
            }
        }

        public void Refresh() => RequestUpdate(immediate: true);

        private void OnParentSizeChanged(object? sender, SizeChangedEventArgs e) => RequestUpdate();

        private void OnElementSizeChanged(object sender, SizeChangedEventArgs e) => RequestUpdate();

        private void OnElementUnloaded(object sender, RoutedEventArgs e) => RequestUpdate();

        private void RequestUpdate(bool immediate = false)
        {
            if (immediate)
            {
                _queued = false;
                Update();
                return;
            }

            if (_queued)
            {
                return;
            }

            _queued = true;
            _element.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _queued = false;
                Update();
            }));
        }

        private void Update()
        {
            var available = Math.Max(0d, _parent.ActualHeight - Padding);
            if (double.IsNaN(available) || double.IsInfinity(available))
            {
                return;
            }

            if (Math.Abs(_lastApplied - available) < 0.5d)
            {
                return;
            }

            _element.MaxHeight = available <= 0 ? 0 : available;
            _lastApplied = available;
        }
    }
}
