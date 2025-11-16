using System.IO;
using System.Windows.Forms;

namespace ClopWindows.App.Infrastructure;

public interface IFolderPicker
{
    string? PickFolder(string? initialPath = null, string? description = null);
}

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialPath = null, string? description = null)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = string.IsNullOrWhiteSpace(description) ? "Select a folder" : description,
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.SelectedPath = initialPath!;
        }

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
