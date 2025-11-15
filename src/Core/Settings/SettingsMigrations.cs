namespace ClopWindows.Core.Settings;

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
        new ClopIgnoreMigration()
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
