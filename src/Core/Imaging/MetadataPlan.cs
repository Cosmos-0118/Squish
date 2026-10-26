using System;
using System.Collections.Generic;
using System.Linq;
using Squish.Core.Optimizers;
using ImageMagick;

namespace Squish.Core.Imaging;

/// <summary>
/// Turns the user's metadata policy into the concrete blobs written to an output file. The default policy
/// ("strip metadata, keep colour profile") removes EXIF/XMP/IPTC (including GPS) but keeps a non-sRGB ICC profile
/// because dropping it would visibly change the colours.
/// </summary>
internal static class MetadataPlan
{
    public static MetadataBlobs Resolve(MetadataBlobs source, MetadataPolicyOptions policy, bool orientationAlreadyApplied)
    {
        byte[]? icc = null;
        if (policy.PreserveColorProfiles && source.Icc is { Length: > 0 } && !IsSrgbEquivalent(source.Icc))
        {
            icc = source.Icc;
        }

        if (!policy.PreserveMetadata)
        {
            return new MetadataBlobs(null, icc, null, null);
        }

        var exif = source.Exif is { Length: > 0 } rawExif ? SanitiseExif(rawExif, policy, orientationAlreadyApplied) : null;

        // XMP can duplicate GPS coordinates; honour the privacy flag by dropping it as well.
        var xmp = policy.StripGpsMetadata ? null : source.Xmp;
        return new MetadataBlobs(exif, icc, xmp, source.Iptc);
    }

    /// <summary>True for the ubiquitous "sRGB IEC61966-2.1" style profiles that add bytes but change nothing.</summary>
    public static bool IsSrgbEquivalent(byte[] icc)
    {
        try
        {
            var profile = new ColorProfile(icc);
            var description = profile.Description ?? string.Empty;
            return description.Contains("sRGB", StringComparison.OrdinalIgnoreCase);
        }
        catch (MagickException)
        {
            return false;
        }
    }

    private static readonly byte[] ExifHeader = { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };

    /// <summary>Magick's <see cref="ExifProfile"/> wants (and returns) the "Exif\0\0" prefix; JPEG APP1 payloads carry it separately.</summary>
    private static byte[] WithExifHeader(byte[] exif)
        => exif.AsSpan().StartsWith(ExifHeader) ? exif : ExifHeader.Concat(exif).ToArray();

    private static byte[]? StripExifHeader(byte[]? exif)
        => exif is null ? null : exif.AsSpan().StartsWith(ExifHeader) ? exif[ExifHeader.Length..] : exif;

    private static byte[]? SanitiseExif(byte[] exifBytes, MetadataPolicyOptions policy, bool orientationAlreadyApplied)
    {
        try
        {
            var profile = new ExifProfile(WithExifHeader(exifBytes));

            if (policy.StripGpsMetadata)
            {
                profile.AllowedIfds = ExifIfds.Ifd0 | ExifIfds.Exif;
                profile.RemoveValue(ExifTag.GPSLatitude);
                profile.RemoveValue(ExifTag.GPSLatitudeRef);
                profile.RemoveValue(ExifTag.GPSLongitude);
                profile.RemoveValue(ExifTag.GPSLongitudeRef);
                profile.RemoveValue(ExifTag.GPSAltitude);
                profile.RemoveValue(ExifTag.GPSAltitudeRef);
            }

            if (policy.AdditionalExifTagsToStrip.Count > 0)
            {
                foreach (var value in profile.Values.Where(v => policy.AdditionalExifTagsToStrip.Contains((int)v.Tag)).ToList())
                {
                    profile.RemoveValue(value.Tag);
                }
            }

            if (orientationAlreadyApplied)
            {
                profile.SetValue(ExifTag.Orientation, (ushort)1);
            }

            // Thumbnails are a classic leak (the embedded preview can still show cropped-out content).
            profile.RemoveThumbnail();

            return StripExifHeader(profile.ToByteArray());
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
