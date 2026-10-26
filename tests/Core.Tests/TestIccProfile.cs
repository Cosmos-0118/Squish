using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Core.Tests;

/// <summary>Builds a tiny but valid matrix/TRC ICC v2 profile (AdobeRGB-like primaries, gamma 2.2) for colour-management tests.</summary>
internal static class TestIccProfile
{
    public const string Description = "Squish Test Wide Gamut RGB";

    public static byte[] Create()
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", DescriptionTag(Description)),
            ("cprt", TextTag("Public domain test profile")),
            ("wtpt", XyzTag(0.9642, 1.0, 0.8249)),
            ("rXYZ", XyzTag(0.60974, 0.31111, 0.01947)),
            ("gXYZ", XyzTag(0.20528, 0.62567, 0.06087)),
            ("bXYZ", XyzTag(0.14919, 0.06322, 0.74457)),
            ("rTRC", GammaTag(2.2)),
            ("gTRC", GammaTag(2.2)),
            ("bTRC", GammaTag(2.2))
        };

        var tableSize = 4 + tags.Count * 12;
        var offset = 128 + tableSize;
        var layout = new List<(string Signature, int Offset, int Size)>();
        foreach (var (signature, data) in tags)
        {
            layout.Add((signature, offset, data.Length));
            offset += (data.Length + 3) & ~3;
        }

        var profile = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(0), (uint)profile.Length);
        Encoding.ASCII.GetBytes("none").CopyTo(profile, 4);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02400000);
        Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2024);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(68), (uint)(0.9642 * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(72), 65536);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(76), (uint)(0.8249 * 65536));

        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = 132 + i * 12;
            Encoding.ASCII.GetBytes(layout[i].Signature).CopyTo(profile, entry);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 4), (uint)layout[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 8), (uint)layout[i].Size);
            tags[i].Data.CopyTo(profile, layout[i].Offset);
        }

        return profile;
    }

    private static byte[] XyzTag(double x, double y, double z)
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), (int)Math.Round(z * 65536));
        return data;
    }

    private static byte[] GammaTag(double gamma)
    {
        var data = new byte[14];
        Encoding.ASCII.GetBytes("curv").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return data;
    }

    private static byte[] TextTag(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[8 + bytes.Length];
        Encoding.ASCII.GetBytes("text").CopyTo(data, 0);
        bytes.CopyTo(data, 8);
        return data;
    }

    private static byte[] DescriptionTag(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[12 + ascii.Length + 4 + 4 + 2 + 1 + 67];
        Encoding.ASCII.GetBytes("desc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(data, 12);
        return data;
    }
}
