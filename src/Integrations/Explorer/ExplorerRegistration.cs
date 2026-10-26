using Microsoft.Win32;
using System;
using System.Runtime.Versioning;

namespace Squish.Integrations.Explorer;

/// <summary>
/// Sets up and removes the registry entries required to surface the Squish Explorer command.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerRegistration
{
    private const string CommandStoreRoot = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\CommandStore\\shell\\";
    private const string FilesRoot = "Software\\Classes\\*\\shell\\";
    private const string DirectoryRoot = "Software\\Classes\\Directory\\shell\\";

    private const string CommandStoreKey = CommandStoreRoot + ExplorerCommandIds.CommandName;
    private const string FilesKey = FilesRoot + ExplorerCommandIds.CommandName;
    private const string DirectoryKey = DirectoryRoot + ExplorerCommandIds.CommandName;

    public static void Register()
    {
        UnregisterLegacy();
        var clsid = "{" + ExplorerCommandIds.ClassIdString + "}";

        using (var commandKey = Registry.CurrentUser.CreateSubKey(CommandStoreKey))
        {
            if (commandKey is not null)
            {
                commandKey.SetValue(null, ExplorerCommandIds.MenuTitle);
                commandKey.SetValue("MUIVerb", ExplorerCommandIds.MenuTitle);
                commandKey.SetValue("ExplorerCommandHandler", clsid);
            }
        }

        RegisterAssociation(FilesKey);
        RegisterAssociation(DirectoryKey);
    }

    public static void Unregister()
    {
        TryDelete(CommandStoreKey);
        TryDelete(FilesKey);
        TryDelete(DirectoryKey);
        UnregisterLegacy();
    }

    /// <summary>Removes the verb registered under the app's name before it was renamed to Squish.</summary>
    private static void UnregisterLegacy()
    {
        TryDelete(CommandStoreRoot + ExplorerCommandIds.LegacyCommandName);
        TryDelete(FilesRoot + ExplorerCommandIds.LegacyCommandName);
        TryDelete(DirectoryRoot + ExplorerCommandIds.LegacyCommandName);
    }

    private static void RegisterAssociation(string keyPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key is null)
        {
            return;
        }

        key.SetValue(null, ExplorerCommandIds.CommandStoreReference);
        key.SetValue("CommandStateSync", string.Empty);
        key.SetValue("MultiSelectModel", "Player");
    }

    private static void TryDelete(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // ignore registry cleanup failures
        }
    }
}
