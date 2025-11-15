namespace ClopWindows.Core.Settings;

internal sealed record ClopFileType(string Name, SettingKey<string[]> DirectoryKey)
{
    public static readonly ClopFileType Images = new("images", SettingsRegistry.ImageDirs);
    public static readonly ClopFileType Videos = new("videos", SettingsRegistry.VideoDirs);
    public static readonly ClopFileType Pdfs = new("pdfs", SettingsRegistry.PdfDirs);

    public static IReadOnlyList<ClopFileType> All { get; } = new[] { Images, Videos, Pdfs };
}
