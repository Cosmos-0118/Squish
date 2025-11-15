using System;
using System.IO;

namespace ClopWindows.Core.Shared;

public static class ClopPaths
{
    private static readonly object Gate = new();
    private static FilePath _workRoot = InitializeWorkRoot();

    public static FilePath WorkRoot
    {
        get
        {
            lock (Gate)
            {
                return _workRoot;
            }
        }
    }

    public static void OverrideWorkRoot(FilePath newRoot)
    {
        lock (Gate)
        {
            _workRoot = newRoot.EnsurePathExists();
        }
    }

    public static FilePath Backups => EnsureDirectory("backups");
    public static FilePath Videos => EnsureDirectory("videos");
    public static FilePath Images => EnsureDirectory("images");
    public static FilePath Pdfs => EnsureDirectory("pdfs");
    public static FilePath Conversions => EnsureDirectory("conversions");
    public static FilePath Downloads => EnsureDirectory("downloads");
    public static FilePath ForResize => EnsureDirectory("for-resize");
    public static FilePath ForFilters => EnsureDirectory("for-filters");
    public static FilePath ProcessLogs => EnsureDirectory("process-logs");
    public static FilePath FinderQuickAction => EnsureDirectory("finder-quick-action");
    public static FilePath SegmentationCache => EnsureDirectory("segmentation-cache");

    private static FilePath EnsureDirectory(string name)
    {
        lock (Gate)
        {
            var path = _workRoot.Append(name);
            path.EnsurePathExists();
            return path;
        }
    }

    private static FilePath InitializeWorkRoot()
    {
        var custom = Environment.GetEnvironmentVariable("CLOP_WORKDIR");
        var basePath = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clop")
            : custom!;
        var workPath = FilePath.From(basePath);
        workPath.EnsurePathExists();
        return workPath;
    }
}
