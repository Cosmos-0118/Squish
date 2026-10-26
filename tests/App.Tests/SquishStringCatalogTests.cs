using Squish.App.Localization;
using Xunit;

namespace Squish.App.Tests;

public sealed class SquishStringCatalogTests
{
    [Fact]
    public void Get_ReturnsKnownString()
    {
        var value = SquishStringCatalog.Get("app.name");
        Assert.Equal("Squish", value);
    }

    [Fact]
    public void Get_ReturnsKeyWhenMissing()
    {
        const string missingKey = "missing.key";
        var value = SquishStringCatalog.Get(missingKey);
        Assert.Equal(missingKey, value);
    }
}
