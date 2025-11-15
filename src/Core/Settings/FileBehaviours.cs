using System.Text.Json.Serialization;

namespace ClopWindows.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConvertedFileBehaviour
{
    Temporary,
    InPlace,
    SameFolder
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptimisedFileBehaviour
{
    Temporary,
    InPlace,
    SameFolder,
    SpecificFolder
}
