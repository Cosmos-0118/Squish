using System;
using System.Windows.Input;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace ClopWindows.App.Views.Settings;

public partial class SettingsView : WpfUserControl
{
    private const double WheelToOffsetFactor = 3.0;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (RootScrollViewer is null || e.Handled || RootScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var targetOffset = RootScrollViewer.VerticalOffset - (e.Delta / WheelToOffsetFactor);
        targetOffset = Math.Clamp(targetOffset, 0, RootScrollViewer.ScrollableHeight);
        if (Math.Abs(targetOffset - RootScrollViewer.VerticalOffset) < 0.01)
        {
            return;
        }

        RootScrollViewer.ScrollToVerticalOffset(targetOffset);
        e.Handled = true;
    }
}
