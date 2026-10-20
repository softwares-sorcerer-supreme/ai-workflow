using AiRunner.Core;

namespace AiRunner.Tests.Core;

public sealed class LogFileRulesTests
{
    [Theory]
    [InlineData("job.log")]
    [InlineData("round-1-copilot-stdout.txt")]
    [InlineData("round-2-transcript.md")]
    [InlineData("error.txt")]
    public void A_plain_file_name_is_accepted(string name)
    {
        Assert.True(LogFileRules.IsPlainName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../job.log")]
    [InlineData("..\\job.log")]
    [InlineData("sub/job.log")]
    [InlineData("sub\\job.log")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\win.ini")]
    [InlineData("C:\\windows\\win.ini")]
    [InlineData("C:win.ini")]
    [InlineData("job.log:stream")]
    [InlineData("job\0.log")]
    [InlineData("job?.log")]
    [InlineData("job*.log")]
    public void Anything_that_is_not_a_plain_file_name_is_refused(string name)
    {
        Assert.False(LogFileRules.IsPlainName(name));
    }

    [Fact]
    public void A_null_name_is_refused()
    {
        Assert.False(LogFileRules.IsPlainName(null));
    }
}
