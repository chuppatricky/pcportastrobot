using AstroHybrid.Linux.Update;
using Xunit;

namespace AstroHybrid.Tests.Update;

public class ForkVersionTests
{
    [Theory]
    [InlineData("1.2.10-linux.1", "1.2.10-linux.2")]
    [InlineData("1.2.10-linux.2", "1.2.10-linux.10")]   // numeric, not lexical
    [InlineData("1.2.10-linux.3", "1.2.11")]            // a new upstream release beats any fork revision
    [InlineData("1.2.10", "1.2.10-linux.1")]            // fork revision comes after its upstream base
    [InlineData("1.2.10-rc1", "1.2.10")]                // real pre-releases come before it
    [InlineData("1.2.10-rc1", "1.2.10-linux.1")]
    [InlineData("1.2.9-linux.7", "1.2.10")]
    [InlineData("v1.2.10-linux.1", "v1.3.0-linux.1")]
    public void Orders(string older, string newer)
    {
        Assert.True(ForkVersion.Compare(older, newer) < 0);
        Assert.True(ForkVersion.Compare(newer, older) > 0);
    }

    [Theory]
    [InlineData("1.2.10-linux.1", "v1.2.10-linux.1")]
    [InlineData("1.2.10-linux.1", "1.2.10-linux.1+0b37ab4c")]
    [InlineData("1.2.10", "1.2.10.0")]
    public void Equal(string a, string b) => Assert.Equal(0, ForkVersion.Compare(a, b));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.x")]
    public void RejectsGarbage(string text) => Assert.False(ForkVersion.TryParse(text, out _));

    [Fact]
    public void AssemblyVersionIsAForkVersionWithoutCommitSuffix()
    {
        var v = AppImageUpdater.AssemblyVersion;
        Assert.DoesNotContain("+", v);
        Assert.True(ForkVersion.TryParse(v, out var parsed));
        Assert.True(parsed.LinuxRevision >= 1);
    }
}
