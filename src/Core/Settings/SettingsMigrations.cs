namespace Squish.Core.Settings;

internal interface ISettingsMigration
{
    int TargetVersion { get; }
    void Apply(SettingsDocument document);
}

internal static class SettingsMigrations
{
    private static readonly ISettingsMigration[] Migrations =
    {
        new SchemaVersionMigration(),
        new SquishIgnoreMigration(),
        new RenamedKeysMigration()
    };

    public static int LatestVersion => Migrations[^1].TargetVersion;

    public static void Run(SettingsDocument document)
    {
        foreach (var migration in Migrations.OrderBy(m => m.TargetVersion))
        {
            if (document.SchemaVersion < migration.TargetVersion)
            {
                migration.Apply(document);
                document.SchemaVersion = migration.TargetVersion;
            }
        }
    }
}

/// <summary>Moves values stored under setting keys that carried the app's former name.</summary>
internal sealed class RenamedKeysMigration : ISettingsMigration
{
    private static readonly (string Legacy, string Current)[] RenamedKeys =
    {
        ("allowClopToAppearInScreenshots", SettingsRegistry.AllowSquishToAppearInScreenshots.Name)
    };

    public int TargetVersion => 3;

    public void Apply(SettingsDocument document)
    {
        foreach (var (legacy, current) in RenamedKeys)
        {
            var node = document.GetValueNode(legacy);
            if (node is null)
            {
                continue;
            }

            if (document.GetValueNode(current) is null)
            {
                document.SetValueNode(current, node.DeepClone());
            }

            document.Values.Remove(legacy);
        }
    }
}

internal sealed class SchemaVersionMigration : ISettingsMigration
{
    public int TargetVersion => 1;

    public void Apply(SettingsDocument document)
    {
        if (document.SchemaVersion <= 0)
        {
            document.SchemaVersion = TargetVersion;
        }
    }
}
