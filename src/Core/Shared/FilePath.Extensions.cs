using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace ClopWindows.Core.Shared;

public readonly partial record struct FilePath
{
    public static FilePath Workdir
    {
        get => ClopPaths.WorkRoot;
        set => ClopPaths.OverrideWorkRoot(value);
    }

    public static FilePath ClopBackups => ClopPaths.Backups;
    public static FilePath Videos => ClopPaths.Videos;
    public static FilePath Images => ClopPaths.Images;
    public static FilePath Pdfs => ClopPaths.Pdfs;
    public static FilePath Conversions => ClopPaths.Conversions;
    public static FilePath Downloads => ClopPaths.Downloads;
    public static FilePath ForResize => ClopPaths.ForResize;
    public static FilePath ForFilters => ClopPaths.ForFilters;
    public static FilePath ProcessLogs => ClopPaths.ProcessLogs;
    public static FilePath FinderQuickAction => ClopPaths.FinderQuickAction;

    public bool IsDirectory
    {
        get
        {
            if (!IOFile.Exists(Value) && !IODirectory.Exists(Value))
            {
                return false;
            }
            var attributes = File.GetAttributes(Value);
            return attributes.HasFlag(FileAttributes.Directory);
        }
    }

    public bool IsRelative => !IOPath.IsPathRooted(Value);

    public Uri ToUri() => new(Value);

    public FilePath WithExtension(string extension)
    {
        var sanitized = extension.StartsWith('.') ? extension : $".{extension}";
        return new FilePath(IOPath.ChangeExtension(Value, sanitized));
    }

    public FilePath WithoutExtension()
    {
        var directory = IOPath.GetDirectoryName(Value) ?? Value;
        var stem = IOPath.Combine(directory, Stem);
        return new FilePath(stem);
    }

    public FilePath ClopBackupPath => FilePath.ClopBackups.Append(NameWithHash);

    public string NameWithHash
    {
        get
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(Value);
            var hash = Convert.ToHexString(sha256.ComputeHash(bytes))[..8];
            return $"{Name}-{hash}";
        }
    }

    public bool IsImage => MediaFormats.IsImage(this);

    public bool IsVideo => MediaFormats.IsVideo(this);

    public bool IsPdf => MediaFormats.IsPdf(this);

    public FilePath TempFile(string? ext = null, bool addUniqueId = false)
    {
        var extension = ext ?? Extension ?? "tmp";
        return FilePath.TempFile(Stem, extension, addUniqueId);
    }

    public static FilePath operator /(FilePath path, string segment) => path.Append(segment);
}
