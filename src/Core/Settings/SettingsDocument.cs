using System.Text.Json.Nodes;

namespace Squish.Core.Settings;

internal sealed class SettingsDocument
{
    private SettingsDocument(JsonObject root)
    {
        Root = root;
        if (root.TryGetPropertyValue("values", out var valuesNode) && valuesNode is JsonObject jsonObject)
        {
            Values = jsonObject;
        }
        else
        {
            Values = new JsonObject();
            Root["values"] = Values;
        }
    }

    public JsonObject Root { get; }

    public JsonObject Values { get; }

    public int SchemaVersion
    {
        get
        {
            if (Root.TryGetPropertyValue("schemaVersion", out var node) && node is not null)
            {
                try
                {
                    return node.GetValue<int>();
                }
                catch
                {
                    return 1;
                }
            }
            return 1;
        }
        set => Root["schemaVersion"] = value;
    }

    public static SettingsDocument CreateNew()
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SettingsMigrations.LatestVersion,
            ["values"] = new JsonObject()
        };
        return new SettingsDocument(root);
    }

    public static SettingsDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return CreateNew();
        }
        using var stream = File.OpenRead(path);
        var root = JsonNode.Parse(stream) as JsonObject;
        return root is null ? CreateNew() : new SettingsDocument(root);
    }

    public JsonNode? GetValueNode(string key) => Values.TryGetPropertyValue(key, out var node) ? node : null;

    public void SetValueNode(string key, JsonNode? node) => Values[key] = node;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a sibling temp file and swap it in: readers (the CLI, a second process, our own file watcher)
        // never observe a truncated config.json, and a crash mid-write cannot corrupt the user's settings.
        var json = Root.ToJsonString(SettingsSerialization.Default);
        var temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (IOException)
        {
            TryDelete(temp);
            File.WriteAllText(path, json);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temp);
            File.WriteAllText(path, json);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
