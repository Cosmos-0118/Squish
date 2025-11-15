using System.Text.Json.Nodes;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

internal sealed class ClopIgnoreMigration : ISettingsMigration
{
    public int TargetVersion => 2;

    public void Apply(SettingsDocument document)
    {
        foreach (var fileType in ClopFileType.All)
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

                var clopIgnore = directoryPath.Append(".clopignore");
                if (!File.Exists(clopIgnore.Value))
                {
                    continue;
                }

                foreach (var other in ClopFileType.All.Where(t => t != fileType))
                {
                    var otherDirs = ReadDirectoryList(document.GetValueNode(other.DirectoryKey.Name));
                    if (!otherDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var target = directoryPath.Append($".clopignore-{other.Name}");
                    try
                    {
                        File.Copy(clopIgnore.Value, target.Value, overwrite: true);
                    }
                    catch (IOException)
                    {
                        // ignore
                    }
                }

                var renamed = directoryPath.Append($".clopignore-{fileType.Name}");
                try
                {
                    if (File.Exists(renamed.Value))
                    {
                        File.Delete(clopIgnore.Value);
                    }
                    else
                    {
                        File.Move(clopIgnore.Value, renamed.Value);
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
