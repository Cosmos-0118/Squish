using Microsoft.Win32;
using System;
using System.Runtime.Versioning;

namespace ClopWindows.Integrations.Explorer;

/// <summary>
/// Sets up and removes the registry entries required to surface the Clop Explorer command.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerRegistration
{
    private const string CommandStoreKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\CommandStore\\shell\\" + ExplorerCommandIds.CommandName;
    private const string FilesKey = "Software\\Classes\\*\\shell\\" + ExplorerCommandIds.CommandName;
    private const string DirectoryKey = "Software\\Classes\\Directory\\shell\\" + ExplorerCommandIds.CommandName;

    public static void Register()
    {
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
