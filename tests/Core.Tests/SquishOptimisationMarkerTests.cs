using System;
using System.IO;
using Squish.Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class SquishOptimisationMarkerTests
{
    [Fact]
    public void MarkerStaysValidAndKeepsTheFilesTimestamp()
    {
        var path = FilePath.TempFile("marker", "txt", addUniqueSuffix: true);
        var stamp = new DateTime(2019, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        try
        {
            File.WriteAllText(path.Value, "payload");
            File.SetLastWriteTimeUtc(path.Value, stamp);

            if (!SquishOptimisationMarker.TryMark(path))
            {
                // Not an NTFS volume: markers are unsupported here, nothing to verify.
                return;
            }

            // Writing the marker stream must not move the modified time, or the marker would invalidate itself.
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path.Value));
            Assert.True(SquishOptimisationMarker.HasValidMarker(path));
        }
        finally
        {
            TestImages.TryDelete(path);
        }
    }
}
