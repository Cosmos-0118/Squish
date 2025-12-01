using System;
using System.IO;
using System.Security.Cryptography;

namespace ClopWindows.Core.Shared;

public readonly record struct FileFingerprint(long Length, string Hash)
{
    public bool IsEmpty => string.IsNullOrEmpty(Hash);

    public string Key => string.IsNullOrEmpty(Hash) ? string.Empty : $"{Length}:{Hash}";

    public static bool TryCreate(FilePath path, out FileFingerprint fingerprint)
    {
        fingerprint = default;

        if (string.IsNullOrWhiteSpace(path.Value) || !File.Exists(path.Value))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 64, FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            var hash = Convert.ToHexString(hashBytes);
            fingerprint = new FileFingerprint(stream.Length, hash);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
