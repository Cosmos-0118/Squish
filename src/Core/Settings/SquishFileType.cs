namespace Squish.Core.Settings;

internal sealed record SquishFileType(string Name, SettingKey<string[]> DirectoryKey)
{
    public static readonly SquishFileType Images = new("images", SettingsRegistry.ImageDirs);
    public static readonly SquishFileType Videos = new("videos", SettingsRegistry.VideoDirs);

    public static IReadOnlyList<SquishFileType> All { get; } = new[] { Images, Videos };
}
