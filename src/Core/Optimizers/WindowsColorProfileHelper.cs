using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClopWindows.Core.Optimizers;

internal static class WindowsColorProfileHelper
{
    private static readonly object Sync = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static WindowsColorProfileSet _cached = WindowsColorProfileSet.Empty;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public static WindowsColorProfileSet GetProfiles(bool allowLookup)
    {
        if (!allowLookup)
        {
            return WindowsColorProfileSet.Empty;
        }

        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _cachedAt <= CacheDuration && _cached != WindowsColorProfileSet.Empty)
            {
                return _cached;
            }

            _cached = ResolveProfiles();
            _cachedAt = now;
            return _cached;
        }
    }

    private static WindowsColorProfileSet ResolveProfiles()
    {
        try
        {
            var rgb = TryGetProfile(ColorProfileSubtype.RgbWorkingSpace) ?? TryGetProfile(ColorProfileSubtype.StandardDisplay);
            var cmyk = TryGetProfile(ColorProfileSubtype.StandardPrinter);
            return new WindowsColorProfileSet(rgb, cmyk);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or IOException)
        {
            return WindowsColorProfileSet.Empty;
        }
    }

    private static string? TryGetProfile(ColorProfileSubtype subtype)
    {
        if (!WcsGetDefaultColorProfileSize(ProfileScope.SystemWide, null, ColorProfileType.Icc, subtype, 0, out var size) || size == 0)
        {
            return null;
        }

        var buffer = new char[size];
        if (!WcsGetDefaultColorProfile(ProfileScope.SystemWide, null, ColorProfileType.Icc, subtype, 0, size, buffer))
        {
            return null;
        }

        var path = new string(buffer).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private enum ProfileScope : uint
    {
        SystemWide = 0x00000002
    }

    private enum ColorProfileType : uint
    {
        Icc = 0x00000000
    }

    private enum ColorProfileSubtype : uint
    {
        None = 0x00000000,
        RgbWorkingSpace = 0x00000001,
        StandardDisplay = 0x00000003,
        StandardPrinter = 0x00000005
    }

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfileSize(
        ProfileScope scope,
        string? deviceName,
        ColorProfileType colorProfileType,
        ColorProfileSubtype colorProfileSubType,
        uint profileId,
        out uint profileNameSize);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfile(
        ProfileScope scope,
        string? deviceName,
        ColorProfileType colorProfileType,
        ColorProfileSubtype colorProfileSubType,
        uint profileId,
        uint profileNameSize,
        char[] profileName);
}

internal readonly record struct WindowsColorProfileSet(string? DefaultRgbProfile, string? DefaultCmykProfile)
{
    public static WindowsColorProfileSet Empty { get; } = new(null, null);

    public bool HasProfiles => !string.IsNullOrWhiteSpace(DefaultRgbProfile) || !string.IsNullOrWhiteSpace(DefaultCmykProfile);
}
