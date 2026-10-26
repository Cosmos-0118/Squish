using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using Squish.App.Infrastructure;
using Squish.App.Services;
using Squish.App.ViewModels;

namespace Squish.App;

public partial class MainWindow : Window
{
    private readonly KeyboardShortcutService _shortcutService;
    private static readonly Uri WindowIconUri = new("pack://application:,,,/Squish;component/Assets/Brand/Squish.ico", UriKind.RelativeOrAbsolute);

    public MainWindow(MainWindowViewModel viewModel, KeyboardShortcutService shortcutService)
    {
        InitializeComponent();
        ApplyWindowIcon();
        DataContext = viewModel;
        ShortcutCatalog.ApplyMainWindowBindings(InputBindings, viewModel);
        _shortcutService = shortcutService;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _shortcutService.Attach(this);
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
}
