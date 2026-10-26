using System.Text.Json.Nodes;
using Squish.Core.Shared;

namespace Squish.Core.Settings;

internal sealed class SquishIgnoreMigration : ISettingsMigration
{
    public int TargetVersion => 2;

    public void Apply(SettingsDocument document)
    {
        foreach (var fileType in SquishFileType.All)
        {
            var directories = ReadDirectoryList(document.GetValueNode(fileType.DirectoryKey.Name));
            foreach (var dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                FilePath directoryPath;
                try
                {
                    directoryPath = FilePath.From(dir);
                }
                catch (Exception)
                {
                    continue;
                }

                var squishIgnore = directoryPath.Append(".squishignore");
                if (!File.Exists(squishIgnore.Value))
                {
                    continue;
                }

                foreach (var other in SquishFileType.All.Where(t => t != fileType))
                {
                    var otherDirs = ReadDirectoryList(document.GetValueNode(other.DirectoryKey.Name));
                    if (!otherDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var target = directoryPath.Append($".squishignore-{other.Name}");
                    try
                    {
                        File.Copy(squishIgnore.Value, target.Value, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // ignore
                    }
                }

                var renamed = directoryPath.Append($".squishignore-{fileType.Name}");
                try
                {
                    if (File.Exists(renamed.Value))
                    {
                        File.Delete(squishIgnore.Value);
                    }
                    else
                    {
                        File.Move(squishIgnore.Value, renamed.Value);
                    }
                }
                catch (IOException)
                {
                    // ignored, best-effort migration
                }
            }
        }
    }

    private static string[] ReadDirectoryList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }
        return array
            .Select(item => item?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
