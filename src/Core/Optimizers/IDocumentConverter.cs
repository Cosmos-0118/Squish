using System.Threading;
using System.Threading.Tasks;
using Squish.Core.Shared;

namespace Squish.Core.Optimizers;

public interface IDocumentConverter
{
    bool Supports(FilePath path, DocumentConversionOptions options);

    Task<DocumentConversionResult> ConvertToPdfAsync(
        FilePath source,
        FilePath workingDirectory,
        DocumentConversionOptions options,
        CancellationToken cancellationToken);
}
