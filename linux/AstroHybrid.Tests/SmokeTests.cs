using AstroHybrid.Linux.Platform;
using Xunit;

public class SmokeTests
{
    [Fact]
    public void WinPathRoundTrips()
    {
        Assert.Equal(@"Z:\home\u\a b", WinPath.ToWindows("/home/u/a b"));
        Assert.Equal("/home/u/a b", WinPath.ToLinux(@"Z:\home\u\a b"));
    }
}
