using System.Windows;
using ClopWindows.App.ViewModels;

namespace ClopWindows.App.Views.Compare;

public partial class CompareView : System.Windows.Controls.UserControl
{
    public CompareView()
    {
        InitializeComponent();
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
}
