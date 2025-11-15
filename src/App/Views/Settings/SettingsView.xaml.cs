using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace ClopWindows.App.Views.Settings;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // ignored; navigation is best-effort.
        }

        e.Handled = true;
    }
}
