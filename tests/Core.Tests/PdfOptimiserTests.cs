using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class PdfOptimiserTests : IDisposable
{
    private readonly List<FilePath> _cleanup = new();

    [Fact]
    public async Task OptimisesPdfViaToolchain()
    {
        var source = Track(CreateSamplePdf());
        var toolchain = new FakePdfToolchain();
        var optimiser = new PdfOptimiser(PdfOptimiserOptions.Default, toolchain);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, source));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        var output = result.OutputPath!.Value;
        Track(output);
        Assert.EndsWith(".clop.pdf", output.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Single(toolchain.Plans);
    }

    [Fact]
    public async Task HonoursMetadataOverrides()
    {
        var source = Track(CreateSamplePdf());
        var toolchain = new FakePdfToolchain();
        var optimiser = new PdfOptimiser(PdfOptimiserOptions.Default, toolchain);

        var metadata = new Dictionary<string, object?>
        {
            ["pdf.aggressive"] = true,
            ["pdf.allowLarger"] = true,
            ["pdf.stripMetadata"] = false,
            ["pdf.fontPath"] = "C:/temp/fonts",
            ["pdf.gsResourcePath"] = "C:/temp/gs",
            ["pdf.linearize"] = false,
            ["pdf.preset"] = "text"
        };

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, source, metadata: metadata));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Track(result.OutputPath ?? source);

        var plan = Assert.Single(toolchain.Plans);
        Assert.True(plan.AggressiveCompression);
        Assert.False(plan.RequireSmallerSize);
        Assert.False(plan.StripMetadata);
        Assert.Equal("C:/temp/fonts", plan.FontPath);
        Assert.Equal("C:/temp/gs", plan.GhostscriptResourcePath);
        Assert.False(plan.EnableLinearisation);
        Assert.Equal(PdfPresetProfile.Text, plan.PresetProfile);
    }

    [Fact]
    public async Task ChoosesGraphicsPresetForImageHeavyPdf()
    {
        var source = Track(CreateImageHeavyPdf());
        var toolchain = new FakePdfToolchain();
        var optimiser = new PdfOptimiser(PdfOptimiserOptions.Default, toolchain);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, source));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Succeeded, result.Status);
        Track(result.OutputPath ?? source);

        var plan = Assert.Single(toolchain.Plans);
        Assert.Equal(PdfPresetProfile.Graphics, plan.PresetProfile);
        Assert.True(plan.EnableLinearisation);
        Assert.True(plan.Insights.ImageCount >= 1);
        Assert.True(plan.Insights.EstimatedMaxImageDpi > 200);
    }

    [Fact]
    public async Task RejectsInvalidPdf()
    {
        var path = Track(CreateInvalidPdf());
        var toolchain = new FakePdfToolchain();
        var optimiser = new PdfOptimiser(PdfOptimiserOptions.Default, toolchain);

        await using var coordinator = new OptimisationCoordinator(new IOptimiser[] { optimiser });
        var ticket = coordinator.Enqueue(new OptimisationRequest(ItemType.Pdf, path));
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OptimisationStatus.Failed, result.Status);
        Assert.Empty(toolchain.Plans);
    }

    public void Dispose()
    {
        foreach (var file in _cleanup)
        {
            TryDelete(file);
        }
    }

    private FilePath Track(FilePath path)
    {
        _cleanup.Add(path);
        return path;
    }

    private static FilePath CreateSamplePdf()
    {
        var path = FilePath.TempFile("pdf-optimiser-test", ".pdf", addUniqueSuffix: true);
        path.EnsureParentDirectoryExists();
        var content = "%PDF-1.4\n" +
                      "1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n" +
                      "2 0 obj\n<</Type /Pages /Kids [3 0 R] /Count 1>>\nendobj\n" +
                      "3 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R>>\nendobj\n" +
                      "4 0 obj\n<</Length 44>>\nstream\nBT /F1 12 Tf 50 150 Td (Hello Clop) Tj ET\nendstream\nendobj\n" +
                      "trailer\n<</Root 1 0 R>>\n%%EOF";
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static FilePath CreateImageHeavyPdf()
    {
        var path = FilePath.TempFile("pdf-optimiser-image", ".pdf", addUniqueSuffix: true);
        path.EnsureParentDirectoryExists();
        var builder = new StringBuilder();
        builder.AppendLine("%PDF-1.4");
        builder.AppendLine("1 0 obj");
        builder.AppendLine("<</Type /Catalog /Pages 2 0 R>>");
        builder.AppendLine("endobj");
        builder.AppendLine("2 0 obj");
        builder.AppendLine("<</Type /Pages /Kids [3 0 R] /Count 2>>");
        builder.AppendLine("endobj");
        builder.AppendLine("3 0 obj");
        builder.AppendLine("<</Type /Page /Parent 2 0 R /Resources << /XObject << /Im0 4 0 R >> >> /MediaBox [0 0 612 792] /Contents 5 0 R>>");
        builder.AppendLine("endobj");
        builder.AppendLine("4 0 obj");
        builder.AppendLine("<</Type /XObject /Subtype /Image /Width 4000 /Height 4000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 12>>");
        builder.AppendLine("stream");
        builder.AppendLine("AAAAAAAAAAAA");
        builder.AppendLine("endstream");
        builder.AppendLine("endobj");
        builder.AppendLine("5 0 obj");
        builder.AppendLine("<</Length 44>>");
        builder.AppendLine("stream");
        builder.AppendLine("BT /F1 12 Tf 50 500 Td (Hello Clop Image) Tj ET");
        builder.AppendLine("endstream");
        builder.AppendLine("endobj");
        builder.AppendLine("trailer");
        builder.AppendLine("<</Root 1 0 R>>");
        builder.AppendLine("%%EOF");
        File.WriteAllText(path.Value, builder.ToString());
        return path;
    }

    private static FilePath CreateInvalidPdf()
    {
        var path = FilePath.TempFile("pdf-optimiser-invalid", ".pdf", addUniqueSuffix: true);
        path.EnsureParentDirectoryExists();
        File.WriteAllText(path.Value, "This is not a PDF");
        return path;
    }

    private static void TryDelete(FilePath path)
    {
        try
        {
            if (File.Exists(path.Value))
            {
                File.Delete(path.Value);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private sealed class FakePdfToolchain : IPdfToolchain
    {
        public List<PdfOptimiserPlan> Plans { get; } = new();

        public Task<PdfToolchainResult> OptimiseAsync(PdfOptimiserPlan plan, FilePath tempOutput, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Plans.Add(plan);
            context.ReportProgress(50, "fake ghostscript");
            File.WriteAllBytes(tempOutput.Value, new byte[256]);
            return Task.FromResult(PdfToolchainResult.Successful());
        }
    }
}
