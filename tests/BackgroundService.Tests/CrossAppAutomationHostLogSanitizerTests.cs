using Squish.BackgroundService.Automation;
using Xunit;

namespace BackgroundService.Tests;

public sealed class CrossAppAutomationHostLogSanitizerTests
{
    [Fact]
    public void SanitizeForLog_StripsLineBreaks()
    {
        var result = CrossAppAutomationHost.SanitizeForLog("/optimise\r\n2026-01-01 FAKE entry");

        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
        Assert.Equal("/optimise2026-01-01 FAKE entry", result);
    }

    [Fact]
    public void SanitizeForLog_ReplacesOtherControlCharacters()
    {
        Assert.Equal("a?b", CrossAppAutomationHost.SanitizeForLog("a\tb"));
    }

    [Fact]
    public void SanitizeForLog_TruncatesLongInput()
    {
        var result = CrossAppAutomationHost.SanitizeForLog(new string('x', 5000));

        Assert.Equal(200, result.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeForLog_HandlesEmpty(string? value)
    {
        Assert.Equal(string.Empty, CrossAppAutomationHost.SanitizeForLog(value));
    }
}
