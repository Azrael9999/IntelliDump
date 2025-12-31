using IntelliDump;

namespace IntelliDump.Tests;

public class OptionsTests
{
    [Fact]
    public void ParsesStringOptions()
    {
        var options = Options.FromArgs(new[] { "dump.dmp", "--strings", "3", "--max-string-length", "120000" });

        Assert.Equal("dump.dmp", options.DumpPath);
        Assert.Equal(3, options.MaxStringsToCapture);
        Assert.Equal(120000, options.MaxStringLength);
    }

    [Fact]
    public void DefaultsToNoStringCapture()
    {
        var options = Options.FromArgs(new[] { "dump.dmp" });

        Assert.Equal(0, options.MaxStringsToCapture);
        Assert.Equal(65536, options.MaxStringLength);
    }
}
