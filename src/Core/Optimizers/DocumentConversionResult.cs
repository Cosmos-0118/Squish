using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Optimizers;

public sealed record DocumentConversionResult(bool Success, FilePath? OutputPath, string? ErrorMessage, FilePath? IntermediatePath = null)
{
    public static DocumentConversionResult Succeeded(FilePath output, FilePath? intermediate = null)
        => new(true, output, null, intermediate);

    public static DocumentConversionResult Failed(string? message, FilePath? intermediate = null)
        => new(false, null, message, intermediate);
}
