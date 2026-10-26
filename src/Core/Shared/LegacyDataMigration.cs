using System;
using System.IO;

namespace Squish.Core.Shared;

/// <summary>
/// Moves per-user data written before the app was renamed (the "Clop" folders under %APPDATA% and
/// %LOCALAPPDATA%) to the Squish folders, so settings, backups, logs, and tool binaries carry over.
/// Runs at most once per process and never overwrites an existing Squish folder.
/// </summary>
public static class LegacyDataMigration
{
    public const string LegacyFolderName = "Clop";
    public const string FolderName = "Squish";

    private static readonly Lazy<bool> Migrated = new(Run);

    public static void EnsureMigrated() => _ = Migrated.Value;

    private static bool Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        MoveIfNeeded(Environment.SpecialFolder.ApplicationData);
        MoveIfNeeded(Environment.SpecialFolder.LocalApplicationData);
        return true;
    }

    private static void MoveIfNeeded(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var legacy = Path.Combine(root, LegacyFolderName);
        var current = Path.Combine(root, FolderName);
        if (!Directory.Exists(legacy) || Directory.Exists(current))
        {
            return;
        }

        try
        {
            Directory.Move(legacy, current);
        }
        catch (IOException)
        {
            // best effort: a locked file leaves the legacy folder in place and Squish starts fresh
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
