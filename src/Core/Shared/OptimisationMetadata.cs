using System.Collections.Generic;

namespace Squish.Core.Shared;

public static class OptimisationMetadata
{
    public const string OutputReplaceOriginal = "output.replaceOriginal";
    public const string OutputDeleteConvertedSource = "output.deleteConvertedSource";
    public const string ImageForceFullOptimisation = "image.forceFullOptimisation";
    public const string ImageAggressive = "image.aggressive";
    public const string VideoAggressive = "video.aggressive";
    public const string DocumentAggressive = "document.aggressive";
    public const string PdfAggressive = "pdf.aggressive";

    /// <summary>Set by container passes (PDF, Office) that already hold a <c>PixelBudget.Shared</c> lease for this picture, so the image job does not take a second one.</summary>
    public const string PixelBudgetHeld = "image.pixelBudgetHeld";

    public static bool GetFlag(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && value is bool flag && flag;
    }
}
