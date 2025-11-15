using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ClopWindows.Integrations.Explorer;

/// <summary>
/// Windows Explorer context menu handler that forwards optimisations to the background automation bridge.
/// </summary>
[ComVisible(true)]
[Guid(ExplorerCommandIds.ClassIdString)]
[ClassInterface(ClassInterfaceType.None)]
[SupportedOSPlatform("windows")]
public sealed class ExplorerOptimiseCommand : IExplorerCommand
{
    [ComRegisterFunction]
    public static void Register(Type _)
    {
        ExplorerRegistration.Register();
    }

    [ComUnregisterFunction]
    public static void Unregister(Type _)
    {
        ExplorerRegistration.Unregister();
    }

    public int GetTitle(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszName)
    {
        ppszName = ExplorerCommandIds.MenuTitle;
        return HResult.S_OK;
    }

    public int GetIcon(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszIcon)
    {
        ppszIcon = null;
        return HResult.S_FALSE;
    }

    public int GetToolTip(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszInfotip)
    {
        ppszInfotip = ExplorerCommandIds.Tooltip;
        return HResult.S_OK;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = ExplorerCommandIds.CanonicalId;
        return HResult.S_OK;
    }

    public int GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out EXPCMDSTATE pCmdState)
    {
        var count = SelectionHelper.CountItems(psiItemArray);
        pCmdState = count == 0 ? EXPCMDSTATE.ECS_HIDDEN : EXPCMDSTATE.ECS_ENABLED;
        return HResult.S_OK;
    }

    public int Invoke(IShellItemArray? psiItemArray, IntPtr hwnd)
    {
        try
        {
            var selection = SelectionHelper.GetSelection(psiItemArray);
            if (selection.Entries.Count == 0)
            {
                return HResult.S_OK;
            }

            var paths = selection.Entries.Select(entry => entry.Path);
            var result = AutomationClient.Optimise(paths, selection.ContainsDirectories);

            if (!result.Success)
            {
                ShowMessage("Clop optimisation failed", result.Message ?? "An unknown error occurred.");
            }
            else if (result.Partial)
            {
                ShowMessage("Clop optimisation completed with warnings", result.Message ?? "Some files could not be optimised.");
            }

            return HResult.S_OK;
        }
        catch (Exception ex)
        {
            ShowMessage("Clop integration error", ex.Message);
            return HResult.E_FAIL;
        }
    }

    public int GetFlags(out EXPCMDFLAGS pFlags)
    {
        pFlags = EXPCMDFLAGS.ECF_DEFAULT;
        return HResult.S_OK;
    }

    public int EnumSubCommands(out IEnumExplorerCommand? ppEnum)
    {
        ppEnum = null;
        return HResult.S_FALSE;
    }

    private static void ShowMessage(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal static class SelectionHelper
{
    public static Selection GetSelection(IShellItemArray? items)
    {
        var entries = new List<SelectionEntry>();
        var containsDirectories = false;

        if (items is null)
        {
            return new Selection(entries, containsDirectories);
        }

        if (items.GetCount(out var count) != HResult.S_OK)
        {
            return new Selection(entries, containsDirectories);
        }

        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) != HResult.S_OK || item is null)
            {
                continue;
            }

            var path = ShellExtensions.GetFileSystemPath(item);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var isDirectory = ShellExtensions.IsDirectory(item);
            entries.Add(new SelectionEntry(path, isDirectory));
            containsDirectories |= isDirectory;
        }

        return new Selection(entries, containsDirectories);
    }

    public static int CountItems(IShellItemArray? items)
    {
        if (items is null)
        {
            return 0;
        }

        return items.GetCount(out var count) == HResult.S_OK ? (int)Math.Min(count, int.MaxValue) : 0;
    }

    internal sealed record Selection(IReadOnlyList<SelectionEntry> Entries, bool ContainsDirectories);

    internal sealed record SelectionEntry(string Path, bool IsDirectory);
}

internal static class ShellExtensions
{
    private const uint SfgaoFolder = 0x20000000;

    public static string? GetFileSystemPath(IShellItem item)
    {
        var hr = item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var namePtr);
        if (hr != HResult.S_OK || namePtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(namePtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    public static bool IsDirectory(IShellItem item)
    {
        return item.GetAttributes(SfgaoFolder, out var attributes) == HResult.S_OK && (attributes & SfgaoFolder) != 0;
    }
}

[ComImport]
[Guid("000214F1-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    [PreserveSig]
    int Next(uint celt, out IntPtr pUICommand, out uint pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IEnumExplorerCommand? ppenum);
}

[ComImport]
[Guid("A08CE4D0-FA25-44AB-B119-4F0CACA8D7E8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszName);

    [PreserveSig]
    int GetIcon(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszIcon);

    [PreserveSig]
    int GetToolTip(IShellItemArray? psiItemArray, IntPtr hwnd, out string? ppszInfotip);

    [PreserveSig]
    int GetCanonicalName(out Guid pguidCommandName);

    [PreserveSig]
    int GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out EXPCMDSTATE pCmdState);

    [PreserveSig]
    int Invoke(IShellItemArray? psiItemArray, IntPtr hwnd);

    [PreserveSig]
    int GetFlags(out EXPCMDFLAGS pFlags);

    [PreserveSig]
    int EnumSubCommands(out IEnumExplorerCommand? ppEnum);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr keyType, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(uint dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem? ppsi);

    [PreserveSig]
    int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetParent(out IShellItem? ppsi);

    [PreserveSig]
    int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
}

[Flags]
public enum EXPCMDFLAGS
{
    ECF_DEFAULT = 0x00000000,
    ECF_HASSUBCOMMANDS = 0x00000020,
    ECF_ISDROPTARGET = 0x00000040,
    ECF_HASSPLITBUTTON = 0x00000080
}

[Flags]
public enum EXPCMDSTATE
{
    ECS_ENABLED = 0x00000000,
    ECS_DISABLED = 0x00000001,
    ECS_HIDDEN = 0x00000002,
    ECS_CHECKBOX = 0x00000004,
    ECS_CHECKED = 0x00000008,
    ECS_RADIOCHECK = 0x00000010
}

public enum SIGDN : uint
{
    SIGDN_FILESYSPATH = 0x80058000
}
