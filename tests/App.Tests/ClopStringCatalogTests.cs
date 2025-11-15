using ClopWindows.App.Localization;
using Xunit;

namespace ClopWindows.App.Tests;

public sealed class ClopStringCatalogTests
{
    [Fact]
    public void Get_ReturnsKnownString()
    {
        var value = ClopStringCatalog.Get("app.name");
        Assert.Equal("Clop", value);
    }

    [Fact]
    public void Get_ReturnsKeyWhenMissing()
    {
        const string missingKey = "missing.key";
        var value = ClopStringCatalog.Get(missingKey);
        Assert.Equal(missingKey, value);
    }
}
