using System;
using ClopWindows.CliBridge;
using Xunit;

namespace CliBridge.Tests;

public class TypeFilterTests
{
    [Fact]
    public void AllowsAllWhenNoIncludeRestrictions()
    {
        var filter = new TypeFilter(Array.Empty<string>(), Array.Empty<string>());

        Assert.True(filter.Allows("png"));
        Assert.True(filter.Allows("mp4"));
        Assert.True(filter.Allows("pdf"));
    }

    [Fact]
    public void ExcludesSpecificExtensions()
    {
        var filter = new TypeFilter(new[] { "image" }, new[] { "png" });

        Assert.True(filter.Allows("jpg"));
        Assert.False(filter.Allows("png"));
        Assert.False(filter.Allows("mp4"));
    }

    [Fact]
    public void ExpandsCommaSeparatedTokens()
    {
        var filter = new TypeFilter(new[] { "image,mp4" }, Array.Empty<string>());

        Assert.True(filter.Allows("jpg"));
        Assert.True(filter.Allows("mp4"));
        Assert.False(filter.Allows("pdf"));
    }
}
