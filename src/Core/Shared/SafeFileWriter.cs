using System;
using System.IO;

namespace Squish.Core.Shared;

/// <summary>
/// Puts a finished file in place without ever leaving a half-written one behind. A plain overwrite-copy truncates the destination
/// first, so a crash, a full disk or a power cut in the middle would leave a broken file where the user's original was.
/// The data goes to a temporary neighbour first and only then replaces the destination in one step.
/// </summary>
internal static class SafeFileWriter
{
    public static void CopyOver(string source, string destination)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".";
        var staging = Path.Combine(directory, $".{Path.GetFileName(destination)}.squish-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, staging, overwrite: false);
            if (File.Exists(destination))
            {
                // Replace keeps the destination's attributes and permissions; a failure leaves the destination untouched.
                File.Replace(staging, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(staging, destination);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: the neighbour is hidden-named and harmless
            }
        }
    }
}
