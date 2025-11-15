using System;
using System.IO;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace ClopWindows.Core.Shared;

/// <summary>
/// Thin wrapper that keeps file system operations explicit and mirrors Swift's FilePath helpers.
/// </summary>
public readonly partial record struct FilePath
{
    public FilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Value = IOPath.GetFullPath(path);
    }

    public string Value { get; }

    public static FilePath From(string path) => new(path);

    public static FilePath Combine(params string[] segments) => new(IOPath.GetFullPath(IOPath.Combine(segments)));

    public FilePath Append(string relativeSegment) => new(IOPath.GetFullPath(IOPath.Combine(Value, relativeSegment)));

    public FilePath Append(FilePath other) => Append(other.Value);

    public FilePath Parent => new(IOPath.GetDirectoryName(Value) ?? Value);

    public string Name => IOPath.GetFileName(Value);

    public string Stem => IOPath.GetFileNameWithoutExtension(Value);

    public string? Extension => IOPath.GetExtension(Value) is { Length: > 1 } ext ? ext[1..] : null;

    public bool Exists => IOFile.Exists(Value) || IODirectory.Exists(Value);

    public FileInfo ToFileInfo() => new(Value);

    public DirectoryInfo ToDirectoryInfo() => new(Value);

    public FilePath EnsureParentDirectoryExists() => EnsureDirectoryExists(IOPath.GetDirectoryName(Value));

    public FilePath EnsureDirectoryExists(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            IODirectory.CreateDirectory(directory!);
        }
        return this;
    }

    public FilePath EnsurePathExists()
    {
        if (IODirectory.Exists(Value) || IOFile.Exists(Value))
        {
            return this;
        }

        if (IOPath.GetExtension(Value).Length == 0)
        {
            IODirectory.CreateDirectory(Value);
        }
        else
        {
            EnsureParentDirectoryExists();
            using (IOFile.Create(Value)) { }
        }

        return this;
    }

    public static FilePath TempFile(string? name = null, string? extension = null, bool addUniqueSuffix = false)
    {
        var baseName = name ?? Guid.NewGuid().ToString("N");
        if (addUniqueSuffix)
        {
            baseName = $"{baseName}-{Guid.NewGuid():N}";
        }

        var ext = extension switch
        {
            null or "" => ".tmp",
            _ when extension.StartsWith('.') => extension,
            _ => "." + extension
        };

        var tempPath = IOPath.Combine(IOPath.GetTempPath(), baseName + ext);
        return new FilePath(tempPath);
    }

    public override string ToString() => Value;
}
