using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ClopWindows.Core.Optimizers;

internal static class ImageFastPathPerceptualValidator
{
    public static async Task<OptimisationResult?> ValidateAsync(OptimisationRequest request, FilePath fastPathOutput, PerceptualGuardOptions guardOptions, CancellationToken token)
    {
        if (!guardOptions.Enabled)
        {
            return null;
        }

        using var original = await Image.LoadAsync<Rgba32>(request.SourcePath.Value, token).ConfigureAwait(false);
        using var candidate = await Image.LoadAsync<Rgba32>(fastPathOutput.Value, token).ConfigureAwait(false);

        var ssim = ImagePerceptualQualityGuard.ComputeStructuralSimilarity(original, candidate);
        if (guardOptions.RejectWhenBelowThreshold && ssim < guardOptions.Threshold)
        {
            return new OptimisationResult(request.RequestId, OptimisationStatus.Succeeded, request.SourcePath, $"Rejected encode below SSIM {guardOptions.Threshold:0.000}");
        }

        return null;
    }
}
