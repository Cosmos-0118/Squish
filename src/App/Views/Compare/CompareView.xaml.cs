using System.Windows;
using System.Windows.Shapes;
using Squish.App.ViewModels;

namespace Squish.App.Views.Compare;

public partial class CompareView : System.Windows.Controls.UserControl
{
    public CompareView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        SetDropZoneActive(e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop));
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        SetDropZoneActive(false);
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFiles(object sender, System.Windows.DragEventArgs e)
    {
        SetDropZoneActive(false);

        if (DataContext is not CompareViewModel viewModel)
        {
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] droppedFiles && droppedFiles.Length > 0)
        {
            viewModel.EnqueueDroppedPaths(droppedFiles);
            e.Handled = true;
        }
    }

    private void SetDropZoneActive(bool active)
    {
        DropZone.SetResourceReference(Shape.FillProperty, active ? "Brush.DropZoneActiveBackground" : "Brush.DropZoneBackground");
        DropZone.SetResourceReference(Shape.StrokeProperty, active ? "Brush.DropZoneActiveBorder" : "Brush.DropZoneBorder");
    }
}
