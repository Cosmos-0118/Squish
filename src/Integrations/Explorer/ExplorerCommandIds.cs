using System;
using System.Runtime.Versioning;

namespace ClopWindows.Integrations.Explorer;

[SupportedOSPlatform("windows")]
internal static class ExplorerCommandIds
{
    public const string CommandName = "Clop.Optimise";
    public const string CommandStoreReference = "CommandStore\\shell\\" + CommandName;
    public const string MenuTitle = "Optimise with Clop";
    public const string Tooltip = "Optimise selected files and folders with Clop.";

    public static readonly Guid ClassId = new("c90cab67-0b3b-4ef7-bc12-3a1a41cc02fa");
    public static readonly Guid CanonicalId = new("7d3ca92d-9a50-4fa7-bc61-21f4d97f4c38");

    public const string ClassIdString = "c90cab67-0b3b-4ef7-bc12-3a1a41cc02fa";
}
